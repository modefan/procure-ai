using MongoDB.Driver;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>
/// MongoDB (single-node replica set, its users from infra/mongo/users.js), RabbitMQ and Redis for the Suppliers tests, with the database
/// created and demo-seeded by the real <c>--migrate</c> run of the API as suppliers_app, as the deployment does it.
/// </summary>
public sealed class SuppliersInfra : IAsyncLifetime
{
    public const string Database = "suppliersdb";
    public const string ApiAssembly = "ProcureAi.Suppliers.Api.dll";

    private readonly MongoFixture _mongo = new();
    private readonly RabbitFixture _rabbit = new();
    private readonly RedisFixture _redis = new();

    /// <summary>suppliersdb as Suppliers' own user suppliers_app, as the service connects.</summary>
    public string DatabaseConnectionString => _mongo.AppConnectionStringFor(Database);

    public string RabbitConnectionString => _rabbit.ConnectionString;

    public string RedisConnectionString => _redis.ConnectionString;

    public IMongoDatabase Db => _mongo.Client.GetDatabase(Database);

    /// <summary>The connection string of another database on the same MongoDB (for a test that must not touch the shared one).</summary>
    public string ConnectionStringFor(string database) => _mongo.ConnectionStringFor(database);

    public IMongoClient Client => _mongo.Client;

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        await _rabbit.InitializeAsync();
        await _redis.InitializeAsync();
        await MigrateAsync();
    }

    /// <summary>
    /// Runs the real <c>Suppliers.Api --migrate</c> with the demo seed (it can be run again: it must not change the result), as Suppliers' own
    /// user suppliers_app (readWrite on suppliersdb only, created by infra/mongo/users.js when the MongoDB fixture started), as deployed.
    /// </summary>
    public Task MigrateAsync() =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, Database, DatabaseConnectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true);

    /// <summary>Runs infra/mongo/users.js again (as root), e.g. with a changed password or without one (see <see cref="MongoUsers"/>).</summary>
    public Task<string> RunUsersScriptAsync(IReadOnlyDictionary<string, string>? environment = null) => _mongo.RunUsersScriptAsync(environment);

    /// <summary>A connection string for <paramref name="database"/> as <paramref name="user"/>.</summary>
    public string ConnectionStringFor(string database, string user, string password) => _mongo.ConnectionStringFor(database, user, password);

    /// <summary>A new client signed in as another user of the admin database (the caller disposes it).</summary>
    public MongoClient ClientAs(string user, string password) => _mongo.ClientAs(user, password);

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _mongo.DisposeAsync();
    }
}
