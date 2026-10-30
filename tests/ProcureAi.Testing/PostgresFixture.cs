using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ProcureAi.Testing;

/// <summary>
/// One Postgres for a test run: the project's custom image (logical WAL + pgaudit, built once from infra/postgres)
/// with the memory and CPU caps of the AppHost's Postgres resource.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // A throwaway test value for the image's init script (it needs some password for the debezium role). Not a secret.
    public const string DebeziumTestPassword = "test-only-debezium";

    // The image's init script creates the debezium role; the server only accepts TCP connections once the script is done.
    private const string ImageName = "procureai-test-postgres:latest";

    private static readonly TimeSpan InitScriptTimeout = TimeSpan.FromSeconds(90);

    private static readonly SemaphoreSlim ImageBuild = new(1, 1);

    private PostgreSqlContainer? _container;

    /// <summary>Optional shared network, set before the fixture starts (Debezium reads the database by <see cref="NetworkAlias"/>).</summary>
    public INetwork? Network { get; init; }

    public string NetworkAlias { get; init; } = "postgres";

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
            .WithEnvironment("DEBEZIUM_PASSWORD", DebeziumTestPassword)
            .WithCommand(
                "-c", "wal_level=logical",
                "-c", "shared_preload_libraries=pgaudit",
                "-c", "pgaudit.log=ddl,role",
                // csvlog, one file per weekday, as the AppHost runs it: the pgaudit summary reads today's file (plan 06 ruling 14).
                "-c", "logging_collector=on",
                "-c", "log_destination=csvlog",
                "-c", "log_filename=postgresql-%a.log",
                "-c", "log_truncate_on_rotation=on",
                "-c", "log_rotation_age=1d",
                "-c", $"shared_buffers={pg["SharedBuffers"]}",
                "-c", $"work_mem={pg["WorkMem"]}")
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Postgres"));
        if (Network is not null)
        {
            builder = builder.WithNetwork(Network).WithNetworkAliases(NetworkAlias);
        }

        _container = builder.Build();
        await _container.StartAsync();
        await WaitForInitScriptAsync();
    }

    /// <summary>Connection string for a database on this server.</summary>
    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>Creates an empty database (dropping a leftover of the same name) and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE); CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
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

    private async Task WaitForInitScriptAsync()
    {
        var deadline = DateTime.UtcNow + InitScriptTimeout;
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname = 'debezium'", connection);
                if ((long)(await command.ExecuteScalarAsync())! > 0)
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("The Postgres init script did not create the debezium role in time.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }
}
