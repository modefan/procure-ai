using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using ProcureAi.Requests.Infrastructure;
using RabbitMQ.Client;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// One Postgres (the project's custom image), RabbitMQ and Redis for the whole test run, from the shared fixtures of
/// <c>ProcureAi.Testing</c> (memory and CPU caps from tests/testcontainers.settings.json), plus the Requests-specific
/// parts: the migrated requests database and Debezium Server.
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    private const string RequestsDb = "requestsdb";

    private const string CdcExchange = "cdc";

    private readonly SemaphoreSlim _debeziumLock = new(1, 1);
    private INetwork? _network;
    private PostgresFixture? _postgres;
    private RabbitFixture? _rabbit;
    private RedisFixture? _redis;
    private DebeziumFixture? _debezium;

    public string RequestsDbConnectionString { get; private set; } = "";

    /// <summary>
    /// The Access service the API's permission checks ask. Every test uses a tenant of its own, so the one stub (with a matrix
    /// per tenant) is shared safely.
    /// </summary>
    public StubAccessServer Access { get; private set; } = null!;

    /// <summary>The Approvals service Requests asks before a submission ("is there an eligible approver?"). Possible for every tenant unless a test says otherwise.</summary>
    public StubApprovalsServer Approvals { get; private set; } = null!;

    /// <summary>The Suppliers catalog Requests prices its lines from (<see cref="StubSuppliersServer.DefaultItems"/> for every tenant until a test sets its own).</summary>
    public StubSuppliersServer Suppliers { get; private set; } = null!;

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

        Access = await StubAccessServer.StartAsync();
        Approvals = await StubApprovalsServer.StartAsync();
        Suppliers = await StubSuppliersServer.StartAsync();

        // The init script only creates the debezium role; the database (as in the AppHost, where Aspire creates it) comes here.
        RequestsDbConnectionString = await _postgres.CreateDatabaseAsync(RequestsDb);
        await DeclareCdcExchangeAsync();
        await RequestsDatabase.MigrateAsync(RequestsDbConnectionString);
    }

    /// <summary>
    /// Starts Debezium Server (once, see <see cref="DebeziumFixture"/>) for the requests database, publishing to the
    /// <c>cdc</c> exchange. Returns when its replication slot is active, so no later change is missed.
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
            await debezium.StartAsync(RequestsDb, "purchase_requests", "request_lines", "audit_entries");
            _debezium = debezium;
        }
        finally
        {
            _debeziumLock.Release();
        }
    }

    /// <summary>A separate Debezium Server on this run's network and Postgres (not started), for tests that control its lifetime.</summary>
    public DebeziumFixture NewDebezium(string offsetsVolume) => new(_network!, _postgres!) { OffsetsVolume = offsetsVolume };

    /// <summary>An empty database (no migrations), for tests with their own model.</summary>
    public Task<string> CreateEmptyDatabaseAsync(string name) => _postgres!.CreateDatabaseAsync(name);

    // Debezium only publishes to an existing exchange (the AppHost declares it the same way).
    private async Task DeclareCdcExchangeAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(RabbitConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(CdcExchange, ExchangeType.Topic, durable: true);
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
        if (Access is not null)
        {
            await Access.DisposeAsync();
        }

        if (Approvals is not null)
        {
            await Approvals.DisposeAsync();
        }

        if (Suppliers is not null)
        {
            await Suppliers.DisposeAsync();
        }

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
