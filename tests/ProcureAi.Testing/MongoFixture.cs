using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace ProcureAi.Testing;

/// <summary>
/// One MongoDB for a test run, started as a <b>single-node replica set</b> like the AppHost's Mongo resource (change streams and
/// transactions need a replica set), with that resource's memory and CPU caps and WiredTiger cache size.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private const string Image = "mongo:8.0";

    private MongoDbContainer? _container;

    /// <summary>A client for the replica set (create databases freely; the container is thrown away with the run).</summary>
    public IMongoClient Client { get; private set; } = null!;

    /// <summary>Connection string of the server, with the replica set and direct connection the container needs.</summary>
    public string ConnectionString => _container!.GetConnectionString();

    /// <summary>Connection string that opens <paramref name="database"/> on this server.</summary>
    public string ConnectionStringFor(string database)
    {
        var url = new MongoUrlBuilder(ConnectionString) { DatabaseName = database };
        if (url.Username is not null)
        {
            // The container's user lives in the admin database; without this the driver would look for it in the one opened.
            url.AuthenticationSource = "admin";
        }

        return url.ToMongoUrl().ToString();
    }

    public async ValueTask InitializeAsync()
    {
        var limits = TestLimits.Section("Mongo");
        _container = new MongoDbBuilder(Image)
            // Testcontainers starts mongod with --replSet and initiates the set itself once it is up.
            .WithReplicaSet()
            .WithCommand("--wiredTigerCacheSizeGB", limits["WiredTigerCacheGb"]!)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Mongo"))
            .Build();
        await _container.StartAsync();
        Client = new MongoClient(ConnectionString);

        // The tests assert on the replica set the AppHost runs; fail loudly if the container is not one (e.g. a command override dropped --replSet).
        var status = await Client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("replSetGetStatus", 1));
        if (status["myState"].ToInt32() != 1)
        {
            throw new InvalidOperationException("The MongoDB test container is not the PRIMARY of a replica set.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
