using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ProcureAi.Testing;

/// <summary>
/// One Postgres for a test run: the project's custom image (logical WAL + pgaudit, built once from infra/postgres)
/// with the memory and CPU caps of the AppHost's Postgres resource. Once the server is up the fixture runs the image's
/// <c>/procureai/roles.sql</c> as deployed (plan 08 ruling 6): every service database with its own roles <c>&lt;service&gt;_app</c> and
/// <c>&lt;service&gt;_cdc</c> (test passwords, <see cref="TestPassword"/>), and Audit's log reader and log tables.
/// <see cref="CreateDatabaseAsync"/> hands out a service database (<see cref="ServiceDatabases"/>) as its <c>_app</c> role, like the
/// deployment; any other database as the superuser, captured as <see cref="TestCdcRole"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The replication role of a database that is not a service database (tests with a database of their own).</summary>
    public const string TestCdcRole = "test_cdc";

    /// <summary>A throwaway test value for <see cref="TestCdcRole"/>. Not a secret.</summary>
    public const string DebeziumTestPassword = "test-only-debezium";

    /// <summary>The service databases of infra/postgres/roles.sql: service name to database. Captured ones also have <c>&lt;service&gt;_cdc</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> ServiceDatabases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["access"] = "accessdb", ["approvals"] = "approvalsdb", ["assistant"] = "assistantdb", ["audit"] = "auditdb",
        ["budgets"] = "budgetsdb", ["invoicing"] = "invoicingdb", ["purchasing"] = "purchasingdb", ["requests"] = "requestsdb",
    };

    /// <summary>The services without a Debezium Server (no <c>_cdc</c> role).</summary>
    private static readonly HashSet<string> Uncaptured = new(StringComparer.Ordinal) { "audit" };

    /// <summary><c>CdcMigrations.CdcRoleSetting</c> (not every test project references BuildingBlocks.Persistence).</summary>
    private const string CdcRoleSetting = "procureai.cdc_role";

    /// <summary>The password the fixture gives a role of roles.sql (<c>requests_app</c>, <c>requests_cdc</c>, <c>audit_log_reader</c>). Not a secret.</summary>
    public static string TestPassword(string role) => "test-only-" + role;

    /// <summary>The service of a service database (<c>requestsdb</c> -> <c>requests</c>), or null for any other database.</summary>
    public static string? ServiceOf(string database) =>
        ServiceDatabases.Where(kv => kv.Value == database).Select(kv => kv.Key).FirstOrDefault();

    /// <summary>The role and password Debezium logs in with for <paramref name="database"/>: its <c>_cdc</c> role, or <see cref="TestCdcRole"/>.</summary>
    public static (string Role, string Password) CdcLoginFor(string database) =>
        ServiceOf(database) is { } service && !Uncaptured.Contains(service)
            ? (service + "_cdc", TestPassword(service + "_cdc"))
            : (TestCdcRole, DebeziumTestPassword);

    private const string ImageName = "procureai-test-postgres:latest";

    private static readonly TimeSpan InitScriptTimeout = TimeSpan.FromSeconds(90);

    private static readonly SemaphoreSlim ImageBuild = new(1, 1);

    private PostgreSqlContainer? _container;

    /// <summary>Optional shared network, set before the fixture starts (Debezium reads the database by <see cref="NetworkAlias"/>).</summary>
    public INetwork? Network { get; init; }

    public string NetworkAlias { get; init; } = "postgres";

    /// <summary>The server's <c>log_filename</c> (as the AppHost sets it); a test of log rotation sets a per-second name instead.</summary>
    public string LogFilename { get; init; } = "postgresql-%a.log";

    private string AdminConnectionString => ConnectionString("postgres");

    public async ValueTask InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(RepoRoot(), "infra", "postgres"))
            .WithDockerfile("Dockerfile")
            .WithName(ImageName)
            .WithDeleteIfExists(false)
            .Build();
        // Fixtures of several test classes start in parallel and all write the same build-context tar: one builds at a time.
        await ImageBuild.WaitAsync();
        try
        {
            await image.CreateAsync();
        }
        finally
        {
            ImageBuild.Release();
        }


        var pg = TestLimits.Section("Postgres");
        var builder = new PostgreSqlBuilder(image)
            .WithCommand(
                "-c", "wal_level=logical",
                "-c", "shared_preload_libraries=pgaudit",
                "-c", "pgaudit.log=ddl,role",
                // csvlog, one file per weekday, as the AppHost runs it: the pgaudit summary reads today's file (plan 06 ruling 14).
                "-c", "logging_collector=on",
                "-c", "log_destination=stderr,csvlog",
                "-c", $"log_filename={LogFilename}",
                "-c", "log_truncate_on_rotation=on",
                "-c", "log_rotation_age=1d",
                "-c", $"shared_buffers={pg["SharedBuffers"]}",
                "-c", $"work_mem={pg["WorkMem"]}",
                "-c", $"max_connections={pg["MaxConnections"]}")
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Postgres"));
        if (Network is not null)
        {
            builder = builder.WithNetwork(Network).WithNetworkAliases(NetworkAlias);
        }

        _container = builder.Build();
        await _container.StartAsync();
        await WaitForInitScriptAsync();
    }

    /// <summary>Connection string for a database on this server, as the superuser.</summary>
    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>Connection string for <paramref name="database"/> as <paramref name="role"/> (a role of roles.sql: its test password).</summary>
    public string ConnectionStringAs(string role, string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString(database)) { Username = role, Password = TestPassword(role) }.ConnectionString;

    /// <summary>
    /// The environment roles.sql reads its passwords from (DB_&lt;SERVICE&gt;_PASSWORD, DB_&lt;SERVICE&gt;_CDC_PASSWORD,
    /// DB_AUDIT_LOG_READER_PASSWORD), with the test passwords. A test may leave some out (<paramref name="without"/>).
    /// </summary>
    public static Dictionary<string, string> RolesScriptEnvironment(params string[] without)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in ServiceDatabases.Keys)
        {
            environment[$"DB_{service.ToUpperInvariant()}_PASSWORD"] = TestPassword(service + "_app");
            if (!Uncaptured.Contains(service))
            {
                environment[$"DB_{service.ToUpperInvariant()}_CDC_PASSWORD"] = TestPassword(service + "_cdc");
            }
        }

        environment["DB_AUDIT_LOG_READER_PASSWORD"] = TestPassword("audit_log_reader");
        foreach (var name in without)
        {
            environment.Remove(name);
        }

        return environment;
    }

    /// <summary>
    /// Runs the image's <c>/procureai/roles.sql</c> with psql as the superuser, as the AppHost's postgres-roles container and the chart's
    /// db-roles Job do, with <paramref name="environment"/> only (default: <see cref="RolesScriptEnvironment"/>). Returns psql's output;
    /// throws with it when psql fails.
    /// </summary>
    public async Task<string> RunRolesScriptAsync(IReadOnlyDictionary<string, string>? environment = null)
    {
        var command = new List<string> { "env", "-i", "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" };
        command.AddRange((environment ?? RolesScriptEnvironment()).Select(kv => $"{kv.Key}={kv.Value}"));
        command.AddRange(["psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "postgres", "-f", "/procureai/roles.sql"]);
        var result = await _container!.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"roles.sql failed ({result.ExitCode}): {result.Stderr} {result.Stdout}");
        }

        return result.Stdout + result.Stderr;
    }

    /// <summary>What the container wrote to its console (stdout and stderr) so far.</summary>
    public async Task<string> ConsoleAsync()
    {
        var (stdout, stderr) = await _container!.GetLogsAsync();
        return stdout + stderr;
    }

    /// <summary>
    /// Creates an empty database (dropping a leftover of the same name) and returns its connection string. A service database
    /// (<see cref="ServiceDatabases"/>) is made by roles.sql, as deployed, and the connection string is its <c>_app</c> role's; any other
    /// database is the superuser's, with <see cref="TestCdcRole"/> as its replication role (<c>procureai.cdc_role</c>).
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            if (ServiceOf(name) is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await create.ExecuteNonQueryAsync();
                await using var setting = new NpgsqlCommand($"ALTER DATABASE \"{name}\" SET {CdcRoleSetting} = '{TestCdcRole}'", connection);
                await setting.ExecuteNonQueryAsync();
            }
        }

        if (ServiceOf(name) is { } service)
        {
            await RunRolesScriptAsync();
            return ConnectionStringAs(service + "_app", name);
        }

        return ConnectionString(name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>The repository root (the folder with <c>ProcureAi.slnx</c>), found from the test output folder.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("ProcureAi.slnx not found above the test output folder.");
    }

    // The server accepts TCP connections once the image's first-start initialisation is done; then come the roles, as deployed, and the
    // replication role of the tests' own databases.
    private async Task WaitForInitScriptAsync()
    {
        var deadline = DateTime.UtcNow + InitScriptTimeout;
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnectionString);
                await connection.OpenAsync();
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        await RunRolesScriptAsync();
        await using var setup = new NpgsqlConnection(AdminConnectionString);
        await setup.OpenAsync();
        await using var role = new NpgsqlCommand($"CREATE ROLE {TestCdcRole} WITH LOGIN REPLICATION PASSWORD '{DebeziumTestPassword}'", setup);
        await role.ExecuteNonQueryAsync();
    }
}
