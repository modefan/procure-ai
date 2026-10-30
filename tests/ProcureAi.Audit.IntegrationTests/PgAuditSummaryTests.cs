using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Infrastructure.PgAudit;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// The pgaudit summary (plan 06 ruling 14): the Postgres server writes csvlog, the <c>--migrate</c> run creates the <c>file_fdw</c> table
/// <c>pgaudit.log_mon</c> ... <c>log_sun</c> in <c>auditdb</c> (readable by <c>audit_log_reader</c> only), and Audit summarises its pgaudit lines into the
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
        // Seven tables over files that do not exist, in a schema of their own (the real tables stay as they are).
        var options = ReaderOptions();
        options.Schema = "pgaudit_missing";
        options.LogFileTemplate = "log/no-such-file-{0}.csv";
        await PgAuditLogSetup.EnsureAsync(
            app.Infra.DbConnectionString, app.Infra.AuditLogConnectionString, options.Schema, options.LogFileTemplate, TestContext.Current.CancellationToken);
        try
        {
            var summary = await new PgAuditReader(app.Infra.AuditLogConnectionString, options, TimeProvider.System)
                .ReadAsync(TestContext.Current.CancellationToken);

            summary.Counts.ShouldBeEmpty();
            summary.Latest.ShouldBeEmpty();
            summary.From.ShouldBe(DateTimeOffset.UtcNow - options.Window, TimeSpan.FromMinutes(1));
        }
        finally
        {
            await ExecuteAsSuperuserAsync("DROP SCHEMA pgaudit_missing CASCADE");
        }

        // The real file is still read.
        var real = await new PgAuditReader(app.Infra.AuditLogConnectionString, ReaderOptions(), TimeProvider.System)
            .ReadAsync(TestContext.Current.CancellationToken);
        real.Counts.ShouldNotBeEmpty("the migration's own DDL is in today's log");
    }

    [Fact]
    public async Task Setup_runs_again_with_an_existing_role_and_tables_and_a_new_password()
    {
        // A reader role and schema of its own, so the real reader keeps its password.
        var role = $"audit_log_reader_{Guid.NewGuid():N}"[..28];
        const string schema = "pgaudit_twice";
        string Reader(string password) => new NpgsqlConnectionStringBuilder(app.Infra.DbConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;

        var options = ReaderOptions();
        options.Schema = schema;
        try
        {
            // An earlier version's leftovers: the role in pg_read_server_files, owning a log_today table.
            await ExecuteAsSuperuserAsync($"""
                CREATE ROLE {role} LOGIN PASSWORD 'first'; GRANT pg_read_server_files TO {role};
                CREATE SCHEMA {schema};
                CREATE FOREIGN TABLE {schema}.log_today (line text) SERVER pgaudit_log OPTIONS (filename 'log/postgresql-Mon.csv');
                ALTER FOREIGN TABLE {schema}.log_today OWNER TO {role};
                """);

            await PgAuditLogSetup.EnsureAsync(app.Infra.DbConnectionString, Reader("first"), schema, options.LogFileTemplate, TestContext.Current.CancellationToken);
            await PgAuditLogSetup.EnsureAsync(app.Infra.DbConnectionString, Reader("second"), schema, options.LogFileTemplate, TestContext.Current.CancellationToken);

            var summary = await new PgAuditReader(Reader("second"), options, TimeProvider.System).ReadAsync(TestContext.Current.CancellationToken);
            summary.Counts.ShouldNotBeEmpty();
            var old = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(Reader("first"), "SELECT 1"));
            old.SqlState.ShouldBe(PostgresErrorCodes.InvalidPassword);
            (await ScalarAsync(app.Infra.DbConnectionString, $"SELECT pg_has_role('{role}', 'pg_read_server_files', 'MEMBER')")).ShouldBe(false);
            (await ScalarAsync(app.Infra.DbConnectionString, $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{schema}' AND c.relkind = 'f'"))
                .ShouldBe(7L, "log_mon ... log_sun, the old log_today is gone");
            (await ScalarAsync(app.Infra.DbConnectionString, $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{schema}' AND c.relowner = (SELECT oid FROM pg_roles WHERE rolname = '{role}')"))
                .ShouldBe(0L);
        }
        finally
        {
            await ExecuteAsSuperuserAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE; REVOKE CONNECT ON DATABASE auditdb FROM {role}; DROP ROLE IF EXISTS {role}");
        }
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
                         "SELECT count(*) FROM pgaudit.log_mon",
                         "SELECT pg_read_file('postmaster.pid')",
                         "CREATE TEMP TABLE leak (line text); COPY leak FROM '/etc/passwd'",
                     })
            {
                var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(appConnection, sql), sql);
                denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
            }

            // audit_log_reader can.
            var today = await TodaysTableAsync();
            await Should.NotThrowAsync(() => ExecuteAsync(app.Infra.AuditLogConnectionString, $"SELECT count(*) FROM {today}"));
            new NpgsqlConnectionStringBuilder(app.Infra.AuditLogConnectionString).Username.ShouldBe("audit_log_reader");
        }
        finally
        {
            await ExecuteAsSuperuserAsync($"REVOKE ALL ON SCHEMA pgaudit FROM {appRole}; REVOKE CONNECT ON DATABASE auditdb FROM {appRole}; DROP ROLE {appRole}");
        }
    }

    [Fact]
    public async Task Reader_can_read_only_the_log_tables()
    {
        // audit_log_reader may SELECT the weekday tables, nothing more: no server files of its own choosing (no pg_read_server_files),
        // no repointing a table (not the owner), no new foreign table.
        foreach (var sql in new[]
                 {
                     "CREATE TEMP TABLE leak (line text); COPY leak FROM '/etc/passwd'",
                     "SELECT pg_read_file('pg_hba.conf')",
                     "ALTER FOREIGN TABLE pgaudit.log_mon OPTIONS (SET filename 'pg_hba.conf')",
                     "ALTER FOREIGN TABLE pgaudit.log_today OPTIONS (SET filename 'pg_hba.conf')",
                     "CREATE FOREIGN TABLE pgaudit.leak (line text) SERVER pgaudit_log OPTIONS (filename 'pg_hba.conf')",
                 })
        {
            var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app.Infra.AuditLogConnectionString, sql), sql);
            denied.SqlState.ShouldBeOneOf([PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.UndefinedTable], sql);
        }

        (await ScalarAsync(app.Infra.AuditLogConnectionString,
                "SELECT pg_has_role(current_user, 'pg_read_server_files', 'USAGE') OR EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'pgaudit' AND c.relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user))"))
            .ShouldBe(false, "the reader neither has pg_read_server_files nor owns a log table");

        // Today's table still reads.
        var today = await TodaysTableAsync();
        await Should.NotThrowAsync(() => ExecuteAsync(app.Infra.AuditLogConnectionString, $"SELECT count(*) FROM {today}"));
    }

    [Fact]
    public async Task Server_log_still_reaches_the_container_console()
    {
        // logging_collector takes stderr into files; the image's entrypoint streams the current stderr file to the console, so the
        // server log still reaches docker logs and the Aspire dashboard.
        var marker = $"procureai-log-probe-{Guid.NewGuid():N}";
        await ExecuteAsSuperuserAsync($"DO $$ BEGIN RAISE LOG '{marker}'; END $$");

        var deadline = DateTime.UtcNow + LogTimeout;
        string console;
        do
        {
            console = await app.Infra.PostgresConsoleAsync();
            if (console.Contains(marker, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"The server log line did not reach the console. Last console lines: {string.Join('\n', console.Split('\n').TakeLast(10))}");
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
        Schema = "pgaudit",
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

    /// <summary>The table over today's file (the weekday in the server's log_timezone, as Postgres names its files).</summary>
    private async Task<string> TodaysTableAsync() =>
        "pgaudit.log_" + (string)(await ScalarAsync(app.Infra.AuditLogConnectionString,
            "SELECT lower(to_char(now() AT TIME ZONE current_setting('log_timezone'), 'Dy'))"))!;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
