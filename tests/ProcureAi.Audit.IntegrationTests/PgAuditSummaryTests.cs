using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Infrastructure.PgAudit;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// The pgaudit summary (plan 06 ruling 14): the Postgres server writes csvlog, the <c>--migrate</c> run creates the <c>file_fdw</c> table
/// <c>pgaudit.log_today</c> in <c>auditdb</c> (readable by <c>audit_log_reader</c> only), and Audit summarises its pgaudit lines into the
/// <c>pgaudit</c> status section. The log is the whole server's, so each test looks for the statements it ran itself.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class PgAuditSummaryTests(AuditApp app)
{
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(15);

    private string Superuser => new NpgsqlConnectionStringBuilder(app.Infra.DbConnectionString).Username!;

    [Fact]
    public async Task Ddl_by_superuser_appears_in_summary()
    {
        var table = $"pgaudit_probe_{Guid.NewGuid():N}"[..30];
        await ExecuteAsSuperuserAsync($"CREATE TABLE {table} (id int)");

        var summary = await WaitForAsync(s => s.Latest.Any(l => l.Text.Contains(table)));

        var statement = summary.Latest.First(l => l.Text.Contains(table));
        statement.Role.ShouldBe(Superuser);
        statement.Database.ShouldBe("auditdb");
        statement.Class.ShouldBe("DDL");
        statement.Command.ShouldBe("CREATE TABLE");
        statement.Text.ShouldBe($"CREATE TABLE {table} (id int)");
        statement.At.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        summary.Counts.ShouldContain(c => c.Role == Superuser && c.Class == "DDL" && c.Count >= 1);
        summary.Latest.Select(l => l.At).ShouldBeInOrder(SortDirection.Descending);
        summary.Latest.Count.ShouldBeLessThanOrEqualTo(10);

        // The status section: counters per class, the statements as items (system-wide: the log has no tenant).
        await using var scope = app.Services.CreateAsyncScope();
        var contributor = scope.ServiceProvider.GetServices<IStatusContributor>().Single(c => c.Name == "pgaudit");
        var section = await contributor.CollectAsync(TestContext.Current.CancellationToken);
        section.Counters[StatusCounters.Key(null, "pgaudit_ddl")].ShouldBeGreaterThanOrEqualTo(1);
        var item = section.Items.Where(i => i.Text.Contains(table)).ShouldHaveSingleItem();
        item.Kind.ShouldBe("pgaudit");
        item.TenantId.ShouldBeNull();
        item.Text.ShouldStartWith($"{Superuser} in auditdb: CREATE TABLE");
    }

    [Fact]
    public async Task Role_change_appears_with_class_role()
    {
        var role = $"pgaudit_role_{Guid.NewGuid():N}"[..28];
        var password = $"secret-{Guid.NewGuid():N}";
        await ExecuteAsSuperuserAsync($"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        try
        {
            var summary = await WaitForAsync(s => s.Latest.Any(l => l.Text.Contains(role)));

            var statement = summary.Latest.First(l => l.Text.Contains(role));
            statement.Class.ShouldBe("ROLE");
            statement.Command.ShouldBe("CREATE ROLE");
            statement.Role.ShouldBe(Superuser);
            statement.Text.ShouldNotContain(password, Case.Sensitive, "pgaudit redacts passwords; the summary must never show one");
            summary.Counts.ShouldContain(c => c.Role == Superuser && c.Class == "ROLE" && c.Count >= 1);
        }
        finally
        {
            await ExecuteAsSuperuserAsync($"DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task Missing_log_file_gives_empty_summary()
    {
        var options = ReaderOptions();
        options.LogFileTemplate = "log/no-such-file-{0}.csv";
        var reader = new PgAuditReader(app.Infra.AuditLogConnectionString, options, TimeProvider.System);

        var summary = await reader.ReadAsync(TestContext.Current.CancellationToken);

        summary.Counts.ShouldBeEmpty();
        summary.Latest.ShouldBeEmpty();
        summary.From.ShouldBe(DateTimeOffset.UtcNow - options.Window, TimeSpan.FromMinutes(1));

        // The real file is still read afterwards (the failed read changed nothing).
        var real = await new PgAuditReader(app.Infra.AuditLogConnectionString, ReaderOptions(), TimeProvider.System)
            .ReadAsync(TestContext.Current.CancellationToken);
        real.Counts.ShouldNotBeEmpty("the migration's own DDL is in today's log");
    }

    [Fact]
    public async Task Audit_app_role_cannot_read_server_files()
    {
        // A role like the one the Audit app would use in a deployment (no superuser, no pg_read_server_files), even with the schema open to it.
        var appRole = $"audit_app_{Guid.NewGuid():N}"[..24];
        const string appPassword = "test-only-app-role";
        await ExecuteAsSuperuserAsync(
            $"CREATE ROLE {appRole} LOGIN PASSWORD '{appPassword}'; GRANT CONNECT ON DATABASE auditdb TO {appRole}; GRANT USAGE ON SCHEMA pgaudit TO {appRole}");
        try
        {
            var appConnection = new NpgsqlConnectionStringBuilder(app.Infra.DbConnectionString)
            {
                Username = appRole,
                Password = appPassword,
                Pooling = false,
            }.ConnectionString;

            foreach (var sql in new[]
                     {
                         "SELECT count(*) FROM pgaudit.log_today",
                         "SELECT pg_read_file('postmaster.pid')",
                         "CREATE TEMP TABLE leak (line text); COPY leak FROM '/etc/passwd'",
                     })
            {
                var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(appConnection, sql), sql);
                denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
            }

            // audit_log_reader can.
            await Should.NotThrowAsync(() => ExecuteAsync(app.Infra.AuditLogConnectionString, "SELECT count(*) FROM pgaudit.log_today"));
            new NpgsqlConnectionStringBuilder(app.Infra.AuditLogConnectionString).Username.ShouldBe("audit_log_reader");
        }
        finally
        {
            await ExecuteAsSuperuserAsync($"REVOKE ALL ON SCHEMA pgaudit FROM {appRole}; REVOKE CONNECT ON DATABASE auditdb FROM {appRole}; DROP ROLE {appRole}");
        }
    }

    [Fact]
    public async Task Statement_text_is_cut_to_200()
    {
        var table = $"pgaudit_long_{Guid.NewGuid():N}"[..29];
        var columns = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"column_number_{i} text"));
        var sql = $"CREATE TABLE {table} ({columns})";
        sql.Length.ShouldBeGreaterThan(400);
        await ExecuteAsSuperuserAsync(sql);

        var summary = await WaitForAsync(s => s.Latest.Any(l => l.Text.Contains(table)));

        var text = summary.Latest.First(l => l.Text.Contains(table)).Text;
        text.Length.ShouldBeLessThanOrEqualTo(200);
        text.Length.ShouldBeGreaterThan(190, "cut at the limit, not shorter");
        sql.ShouldStartWith(text.TrimEnd('…'));
    }

    private PgAuditOptions ReaderOptions() => new()
    {
        ConnectionName = "auditlog",
        LogFileTemplate = "log/postgresql-{0}.csv",
        Window = TimeSpan.FromDays(1),
        LatestCount = 10,
        TextMaxLength = 200,
    };

    /// <summary>The registered reader (what the status section uses), until the log shows what the test did.</summary>
    private async Task<PgAuditSummary> WaitForAsync(Func<PgAuditSummary, bool> done)
    {
        var reader = app.Services.GetRequiredService<IPgAuditReader>();
        var deadline = DateTime.UtcNow + LogTimeout;
        PgAuditSummary summary;
        do
        {
            summary = await reader.ReadAsync(TestContext.Current.CancellationToken);
            if (done(summary))
            {
                return summary;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"The pgaudit summary did not show the statement. Latest: {string.Join(" | ", summary.Latest.Select(l => l.Text))}");
    }

    private Task ExecuteAsSuperuserAsync(string sql) => ExecuteAsync(app.Infra.DbConnectionString, sql);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
