using MongoDB.Driver;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>
/// MongoDB (single-node replica set), RabbitMQ and Redis for the Suppliers tests, with the database created and demo-seeded by the real
/// <c>--migrate</c> run of the API, as the deployment does it.
/// </summary>
public sealed class SuppliersInfra : IAsyncLifetime
{
    public const string Database = "suppliersdb";
    public const string ApiAssembly = "ProcureAi.Suppliers.Api.dll";

    private readonly MongoFixture _mongo = new();
    private readonly RabbitFixture _rabbit = new();
    private readonly RedisFixture _redis = new();

    public string DatabaseConnectionString => _mongo.ConnectionStringFor(Database);

    public string RabbitConnectionString => _rabbit.ConnectionString;

    public string RedisConnectionString => _redis.ConnectionString;

    public IMongoDatabase Db => _mongo.Client.GetDatabase(Database);

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        await _rabbit.InitializeAsync();
        await _redis.InitializeAsync();
        await MigrateAsync();
    }

    /// <summary>Runs the real <c>Suppliers.Api --migrate</c> with the demo seed (it can be run again: it must not change the result).</summary>
    public Task MigrateAsync() =>
        ApiMigrateProcess.RunAsync(ApiAssembly, Database, DatabaseConnectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true);

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _mongo.DisposeAsync();
    }
}
