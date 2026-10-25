using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using RabbitMQ.Client;

namespace ProcureAi.Testing;

/// <summary>
/// The infrastructure of one service's integration tests, shared by every service so none copies it: Postgres (the project's
/// custom image), RabbitMQ and Redis from the fixtures above (memory and CPU caps from tests/testcontainers.settings.json),
/// the service's migrated database, the <c>cdc</c> exchange, and Debezium Server for that database on demand.
/// A service derives from it, says how its database is migrated, and registers it as an xUnit collection fixture.
/// </summary>
/// <param name="databaseName">The service's database, e.g. <c>accessdb</c> (also the Debezium topic prefix).</param>
/// <param name="capturedTables">The tables Debezium captures (the ones its migration put into the publication).</param>
public abstract class ServiceInfraFixture(string databaseName, params string[] capturedTables) : IAsyncLifetime
{
    public const string CdcExchange = "cdc";

    private readonly SemaphoreSlim _debeziumLock = new(1, 1);
    private INetwork? _network;
    private PostgresFixture? _postgres;
    private RabbitFixture? _rabbit;
    private RedisFixture? _redis;
    private DebeziumFixture? _debezium;

    /// <summary>The service's database, migrated by <see cref="MigrateAsync"/>.</summary>
    public string DbConnectionString { get; private set; } = "";

    public string RabbitConnectionString => _rabbit!.ConnectionString;

    public string RedisConnectionString => _redis!.ConnectionString;

    /// <summary>Migrates (and seeds, if the service wants that in tests) a database on the running infrastructure.</summary>
    protected abstract Task MigrateAsync(string connectionString);

    public async ValueTask InitializeAsync()
    {
        // Debezium reaches Postgres and RabbitMQ by the host names debezium/application.properties uses ("postgres", "rabbitmq").
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgresFixture { Network = _network };
        _rabbit = new RabbitFixture { Network = _network };
        _redis = new RedisFixture();
        await Task.WhenAll(_postgres.InitializeAsync().AsTask(), _rabbit.InitializeAsync().AsTask(), _redis.InitializeAsync().AsTask());

        // The init script only creates the debezium role; the database (as in the AppHost, where Aspire creates it) comes here.
        DbConnectionString = await _postgres.CreateDatabaseAsync(databaseName);
        await DeclareCdcExchangeAsync();
        await MigrateAsync(DbConnectionString);
    }

    /// <summary>
    /// Starts Debezium Server (once) for the service's database, publishing to the <c>cdc</c> exchange. Returns when its
    /// replication slot is active, so no later change is missed.
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

            var debezium = new DebeziumFixture(_network!, _postgres!);
            await debezium.StartAsync(databaseName, capturedTables);
            _debezium = debezium;
        }
        finally
        {
            _debeziumLock.Release();
        }
    }

    /// <summary>An empty database (no migrations), for tests that migrate it themselves.</summary>
    public Task<string> CreateEmptyDatabaseAsync(string name) => _postgres!.CreateDatabaseAsync(name);

    // Debezium only publishes to an existing exchange (the AppHost declares it the same way).
    private async Task DeclareCdcExchangeAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(RabbitConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(CdcExchange, ExchangeType.Topic, durable: true);
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
