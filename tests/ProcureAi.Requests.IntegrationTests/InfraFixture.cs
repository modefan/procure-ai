using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using ProcureAi.Requests.Infrastructure;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// One Postgres (the project's custom image: logical WAL + pgaudit, built from infra/postgres) and one RabbitMQ
/// for the whole test run, with the memory and CPU caps from tests/testcontainers.settings.json.
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    // A throwaway test value for the image's init script (it needs some password for the debezium role). Not a secret.
    private const string DebeziumTestPassword = "test-only-debezium";
    private const string RequestsDb = "requestsdb";
    private const string RabbitImage = "rabbitmq:4.3-management";

    private static readonly IConfigurationSection Limits = new ConfigurationBuilder()
        .AddJsonFile("testcontainers.settings.json", optional: false)
        .Build()
        .GetSection("Resources");

    private const string DebeziumImage = "quay.io/debezium/server:3.7.0.Final";
    private const string CdcExchange = "cdc";
    private const string DebeziumSlot = "debezium_requests";

    // The host names debezium/application.properties uses; the containers share a network under these names.
    private const string PostgresAlias = "postgres";
    private const string RabbitAlias = "rabbitmq";

    private readonly SemaphoreSlim _debeziumLock = new(1, 1);
    private INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbit;
    private IContainer? _debezium;

    public string RequestsDbConnectionString { get; private set; } = "";

    public string RabbitConnectionString => _rabbit!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(RepoRoot(), "infra", "postgres"))
            .WithDockerfile("Dockerfile")
            .WithName("procureai-test-postgres:latest")
            .WithDeleteIfExists(false)
            .Build();
        await image.CreateAsync();

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        var pg = Limits.GetSection("Postgres");
        _postgres = new PostgreSqlBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases(PostgresAlias)
            .WithEnvironment("DEBEZIUM_PASSWORD", DebeziumTestPassword)
            .WithCommand(
                "-c", "wal_level=logical",
                "-c", "shared_preload_libraries=pgaudit",
                "-c", "pgaudit.log=ddl,role",
                "-c", $"shared_buffers={pg["SharedBuffers"]}",
                "-c", $"work_mem={pg["WorkMem"]}")
            .WithCreateParameterModifier(p => ApplyLimits(p, pg))
            .Build();

        var rq = Limits.GetSection("RabbitMq");
        _rabbit = new RabbitMqBuilder(RabbitImage)
            .WithNetwork(_network)
            .WithNetworkAliases(RabbitAlias)
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", $"-rabbit vm_memory_high_watermark {{absolute,\"{rq["VmMemoryHighWatermarkAbsolute"]}\"}}")
            .WithCreateParameterModifier(p => ApplyLimits(p, rq))
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        RequestsDbConnectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = RequestsDb,
        }.ConnectionString;
        await WaitForInitScriptAsync();
        await DeclareCdcExchangeAsync();
        await RequestsDatabase.MigrateAsync(RequestsDbConnectionString);
    }

    /// <summary>
    /// Starts Debezium Server (once) with the real infra/debezium/application.properties, reading the requests database
    /// and publishing to the <c>cdc</c> exchange. Returns when its replication slot is active, so no later change is missed.
    /// </summary>
    public async Task EnsureDebeziumAsync()
    {
        await _debeziumLock.WaitAsync();
        try
        {
            if (_debezium is not null)
            {
                return;
            }

            var dbz = Limits.GetSection("Debezium");
            var container = new ContainerBuilder(DebeziumImage)
                .WithNetwork(_network)
                .WithBindMount(
                    Path.Combine(RepoRoot(), "infra", "debezium", "application.properties"),
                    "/debezium/config/application.properties",
                    AccessMode.ReadOnly)
                .WithEnvironment("JAVA_OPTS", dbz["JavaOpts"])
                .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", DebeziumTestPassword)
                .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", RabbitMqBuilder.DefaultUsername)
                .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", RabbitMqBuilder.DefaultPassword)
                .WithPortBinding(8080, true)
                .WithCreateParameterModifier(p => ApplyLimits(p, dbz))
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/q/health").ForPort(8080)))
                .Build();
            _debezium = container;
            await container.StartAsync();
            await WaitForReplicationSlotAsync();
        }
        finally
        {
            _debeziumLock.Release();
        }
    }

    /// <summary>An empty database (no migrations), for tests with their own model.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(RequestsDbConnectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE); CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(RequestsDbConnectionString) { Database = name }.ConnectionString;
    }

    // Debezium only publishes to an existing exchange (the AppHost declares it the same way).
    private async Task DeclareCdcExchangeAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(RabbitConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(CdcExchange, ExchangeType.Topic, durable: true);
    }

    private async Task WaitForReplicationSlotAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await using var connection = new NpgsqlConnection(RequestsDbConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_replication_slots WHERE slot_name = @slot AND active", connection);
            command.Parameters.AddWithValue("slot", DebeziumSlot);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Debezium did not start streaming within 60 s.");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>A fresh, migrated database, so a test can have its own Wolverine message store.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(RequestsDbConnectionString) { Database = "postgres" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(RequestsDbConnectionString) { Database = name }.ConnectionString;
        await RequestsDatabase.MigrateAsync(connectionString);
        return connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debezium is not null)
        {
            await _debezium.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }

    private static void ApplyLimits(Docker.DotNet.Models.CreateContainerParameters parameters, IConfigurationSection section)
    {
        var host = parameters.HostConfig!;
        host.Memory = long.Parse(section["MemoryMb"]!) * 1024 * 1024;
        host.MemorySwap = host.Memory;
        host.NanoCPUs = long.Parse(section["Cpus"]!) * 1_000_000_000L;
    }

    // The image's init script creates the requests database after the server first accepts local connections.
    private async Task WaitForInitScriptAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(RequestsDbConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("ProcureAi.slnx not found above the test output folder.");
    }
}

[CollectionDefinition(Name)]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>
{
    public const string Name = "requests-infra";
}
