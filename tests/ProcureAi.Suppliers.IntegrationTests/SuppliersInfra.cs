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

    /// <summary>The databases the <c>debezium</c> MongoDB user may read, as the AppHost passes them (every captured MongoDB database).</summary>
    public static readonly string[] DebeziumDatabases = [Database, "assistantchatdb"];

    /// <summary>The password --migrate gives the <c>debezium</c> MongoDB user (<c>Mongo:DebeziumPassword</c>; generated per run).</summary>
    public string DebeziumPassword { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Runs the real <c>Suppliers.Api --migrate</c> with the demo seed (it can be run again: it must not change the result). As under the
    /// AppHost it also creates the least-privilege <c>debezium</c> user (plan 07 Task 4) with <see cref="DebeziumPassword"/>.
    /// </summary>
    public Task MigrateAsync()
    {
        var environment = new Dictionary<string, string> { ["Mongo__DebeziumPassword"] = DebeziumPassword };
        for (var i = 0; i < DebeziumDatabases.Length; i++)
        {
            environment[$"Mongo__DebeziumDatabases__{i}"] = DebeziumDatabases[i];
        }

        return ApiMigrateProcess.RunAsync(
            ApiAssembly, Database, DatabaseConnectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true, environment: environment);
    }

    /// <summary>Runs <c>--migrate</c> with exactly these settings (no Debezium user settings unless given), e.g. to check it refuses to run.</summary>
    public Task MigrateWithAsync(IReadOnlyDictionary<string, string> environment) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, Database, DatabaseConnectionString, RabbitConnectionString, RedisConnectionString, demoSeed: false, environment: environment);

    /// <summary>A new client signed in as another user of the admin database (the caller disposes it).</summary>
    public MongoClient ClientAs(string user, string password) => _mongo.ClientAs(user, password);

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _mongo.DisposeAsync();
    }
}
