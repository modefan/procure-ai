using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using ProcureAi.Requests.Infrastructure;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// One Postgres (the project's custom image), RabbitMQ and Redis for the whole test run, from the shared fixtures of
/// <c>ProcureAi.Testing</c> (memory and CPU caps from tests/testcontainers.settings.json), plus the Requests-specific
/// parts: the migrated requests database and Debezium Server.
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    private const string RequestsDb = "requestsdb";

    private const string DebeziumImage = "quay.io/debezium/server:3.7.0.Final";
    private const string CdcExchange = "cdc";
    private const string DebeziumSlot = "debezium_requests";

    private readonly SemaphoreSlim _debeziumLock = new(1, 1);
    private INetwork? _network;
    private PostgresFixture? _postgres;
    private RabbitFixture? _rabbit;
    private RedisFixture? _redis;
    private IContainer? _debezium;

    public string RequestsDbConnectionString { get; private set; } = "";

    public string RabbitConnectionString => _rabbit!.ConnectionString;

    public string RedisConnectionString => _redis!.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        // Debezium reaches Postgres and RabbitMQ by the host names debezium/application.properties uses ("postgres", "rabbitmq").
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgresFixture { Network = _network };
        _rabbit = new RabbitFixture { Network = _network };
        _redis = new RedisFixture();
        await Task.WhenAll(_postgres.InitializeAsync().AsTask(), _rabbit.InitializeAsync().AsTask(), _redis.InitializeAsync().AsTask());

        RequestsDbConnectionString = _postgres.ConnectionString(RequestsDb);
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

            var dbz = TestLimits.Section("Debezium");
            var container = new ContainerBuilder(DebeziumImage)
                .WithNetwork(_network)
                .WithBindMount(
                    Path.Combine(PostgresFixture.RepoRoot(), "infra", "debezium", "application.properties"),
                    "/debezium/config/application.properties",
                    AccessMode.ReadOnly)
                .WithEnvironment("JAVA_OPTS", dbz["JavaOpts"])
                .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", PostgresFixture.DebeziumTestPassword)
                .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", RabbitMqBuilder.DefaultUsername)
                .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", RabbitMqBuilder.DefaultPassword)
                .WithPortBinding(8080, true)
                .WithCreateParameterModifier(p => TestLimits.Apply(p, "Debezium"))
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
    public Task<string> CreateEmptyDatabaseAsync(string name) => _postgres!.CreateDatabaseAsync(name);

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
        var connectionString = await _postgres!.CreateDatabaseAsync(name);
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

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>
{
    public const string Name = "requests-infra";
}
