using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using RabbitMQ.Client;

namespace ProcureAi.Testing;

/// <summary>
/// The infrastructure of one service's integration tests, shared by every service so none copies it: Postgres (the project's
/// custom image), RabbitMQ and Redis from the fixtures above (memory and CPU caps from tests/testcontainers.settings.json),
/// the service's migrated database, the <c>cdc</c> exchange, and Debezium Server for that database on demand. A service with a second
/// database on MongoDB (the Assistant) sets <see cref="UsesMongo"/>, and a MongoDB replica set starts too (<see cref="MongoConnectionStringFor"/>).
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
    private MongoFixture? _mongo;
    private DebeziumFixture? _debezium;
    private MongoDebeziumFixture? _mongoDebezium;
    private readonly SemaphoreSlim _mongoLock = new(1, 1);
    private readonly List<DebeziumFixture> _otherDebeziums = [];

    /// <summary>The service's database, migrated by <see cref="MigrateAsync"/>.</summary>
    public string DbConnectionString { get; private set; } = "";

    public string RabbitConnectionString => _rabbit!.ConnectionString;

    /// <summary>RabbitMQ's management HTTP API (same user and password as the connection string).</summary>
    public Uri RabbitManagementUri => _rabbit!.ManagementUri;

    public string RedisConnectionString => _redis!.ConnectionString;

    /// <summary>True for a service that also keeps a database on MongoDB: the fixture then starts <see cref="MongoFixture"/> as well.</summary>
    protected virtual bool UsesMongo => false;

    /// <summary>The MongoDB server (only when <see cref="UsesMongo"/>).</summary>
    public MongoFixture Mongo => _mongo ?? throw new InvalidOperationException($"{GetType().Name} does not use MongoDB (override {nameof(UsesMongo)}).");

    /// <summary>A connection string that opens <paramref name="database"/> on the MongoDB server (only when <see cref="UsesMongo"/>).</summary>
    public string MongoConnectionStringFor(string database) => Mongo.ConnectionStringFor(database);

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
        var starting = new List<Task> { _postgres.InitializeAsync().AsTask(), _rabbit.InitializeAsync().AsTask(), _redis.InitializeAsync().AsTask() };
        if (UsesMongo)
        {
            _mongo = new MongoFixture { Network = _network };
            starting.Add(_mongo.InitializeAsync().AsTask());
        }

        await Task.WhenAll(starting);

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

    /// <summary>
    /// Another service's database on the same Postgres, migrated by <paramref name="migrate"/>, with its own Debezium Server
    /// capturing <paramref name="tables"/> (as the AppHost gives every captured database). For a service that reads other
    /// services' changes (Audit). Returns the connection string once Debezium streams the database.
    /// </summary>
    public async Task<string> AddCapturedDatabaseAsync(string database, Func<string, Task> migrate, params string[] tables)
    {
        var connectionString = await _postgres!.CreateDatabaseAsync(database);
        await migrate(connectionString);
        var debezium = new DebeziumFixture(_network!, _postgres!);
        lock (_otherDebeziums)
        {
            _otherDebeziums.Add(debezium);
        }

        await debezium.StartAsync(database, tables);
        return connectionString;
    }

    /// <summary>
    /// MongoDB change data capture (plan 06 ruling 16), once per run: the MongoDB replica set (started now when the service does not use
    /// MongoDB itself) and the MongoDB Debezium Server capturing <paramref name="collections"/> (<c>&lt;database&gt;.&lt;collection&gt;</c>), as
    /// the AppHost's <c>debezium-mongo</c>. For a service that reads the MongoDB changes (Audit). Returns once the change stream is open.
    /// </summary>
    public async Task<MongoFixture> EnsureMongoCdcAsync(params string[] collections)
    {
        await _mongoLock.WaitAsync();
        try
        {
            if (_mongo is null)
            {
                var mongo = new MongoFixture { Network = _network };
                await mongo.InitializeAsync();
                _mongo = mongo;
            }

            if (_mongoDebezium is null)
            {
                var debezium = new MongoDebeziumFixture(_network!, _mongo);
                _mongoDebezium = debezium;
                await debezium.StartAsync(collections);
            }

            return _mongo;
        }
        finally
        {
            _mongoLock.Release();
        }
    }

    /// <summary>An empty database (no migrations), for tests that migrate it themselves.</summary>
    public Task<string> CreateEmptyDatabaseAsync(string name) => _postgres!.CreateDatabaseAsync(name);

    /// <summary>What the Postgres container wrote to its console (stdout and stderr) so far.</summary>
    public Task<string> PostgresConsoleAsync() => _postgres!.ConsoleAsync();

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

        if (_mongoDebezium is not null)
        {
            await _mongoDebezium.DisposeAsync();
        }

        foreach (var debezium in _otherDebeziums)
        {
            await debezium.DisposeAsync();
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

        if (_mongo is not null)
        {
            await _mongo.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }
}
