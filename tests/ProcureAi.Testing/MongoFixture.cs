using DotNet.Testcontainers.Networks;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace ProcureAi.Testing;

/// <summary>
/// One MongoDB for a test run, started as a <b>single-node replica set</b> like the AppHost's Mongo resource (change streams and
/// transactions need a replica set), with that resource's memory and CPU caps and WiredTiger cache size. Once it is PRIMARY the fixture runs
/// infra/mongo/users.js as deployed (plan 08 ruling 6), so the users of <see cref="MongoUsers"/> exist with their test passwords.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private const string Image = "mongo:8.0";

    private MongoDbContainer? _container;

    /// <summary>The replica set Testcontainers initiates (as <c>127.0.0.1:27017</c>, so other containers connect with <c>directConnection=true</c>).</summary>
    public const string ReplicaSetName = "rs0";

    /// <summary>The port MongoDB listens on inside its container.</summary>
    public const int ContainerPort = 27017;

    /// <summary>Optional shared network, set before the fixture starts (Debezium reads the change streams by <see cref="NetworkAlias"/>).</summary>
    public INetwork? Network { get; init; }

    public string NetworkAlias { get; init; } = "mongo";

    /// <summary>A client for the replica set (create databases freely; the container is thrown away with the run).</summary>
    public IMongoClient Client { get; private set; } = null!;

    /// <summary>Connection string of the server, with the replica set and direct connection the container needs.</summary>
    public string ConnectionString => _container!.GetConnectionString();

    /// <summary>
    /// Connection string that opens <paramref name="database"/> as the user that owns it (<see cref="MongoUsers.UserOf"/>: suppliersdb as
    /// suppliers_app, assistantchatdb as assistant_app), as the services connect; any other database as root.
    /// </summary>
    public string AppConnectionStringFor(string database) =>
        MongoUsers.UserOf.TryGetValue(database, out var user) ? ConnectionStringFor(database, user, MongoUsers.TestPassword(user)) : ConnectionStringFor(database);

    /// <summary>Connection string that opens <paramref name="database"/> as <paramref name="user"/> (a user of the admin database).</summary>
    public string ConnectionStringFor(string database, string user, string password) =>
        new MongoUrlBuilder(ConnectionString)
        {
            DatabaseName = database, Username = user, Password = password, AuthenticationSource = "admin",
        }.ToMongoUrl().ToString();

    /// <summary>
    /// Runs infra/mongo/users.js in the container with mongosh, signed in as root, with only <paramref name="environment"/> (default:
    /// <see cref="MongoUsers.ScriptEnvironment"/>) besides the root sign-in, as the AppHost's mongo-users container and the chart's db-roles
    /// Job do. Returns mongosh's output; throws with it when the script fails.
    /// </summary>
    public async Task<string> RunUsersScriptAsync(IReadOnlyDictionary<string, string>? environment = null)
    {
        var script = await File.ReadAllBytesAsync(Path.Combine(PostgresFixture.RepoRoot(), "infra", "mongo", "users.js"));
        await _container!.CopyAsync(script, "/tmp/procureai-users.js");
        var root = new MongoUrl(ConnectionString);
        var command = new List<string>
        {
            "env", "-i", "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "HOME=/tmp",
            $"MONGO_USER={root.Username}", $"MONGO_PASSWORD={root.Password}",
        };
        command.AddRange((environment ?? MongoUsers.ScriptEnvironment()).Select(kv => $"{kv.Key}={kv.Value}"));
        command.AddRange(["mongosh", "--quiet", "--norc", $"mongodb://127.0.0.1:{ContainerPort}/?directConnection=true", "/tmp/procureai-users.js"]);
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"users.js failed ({result.ExitCode}): {result.Stderr} {result.Stdout}");
        }

        return result.Stdout + result.Stderr;
    }

    /// <summary>Connection string that opens <paramref name="database"/> on this server, as root.</summary>
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
        var builder = new MongoDbBuilder(Image)
            // Testcontainers starts mongod with --replSet and initiates the set itself once it is up.
            .WithReplicaSet(ReplicaSetName)
            .WithCommand("--wiredTigerCacheSizeGB", limits["WiredTigerCacheGb"]!)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Mongo"));
        if (Network is not null)
        {
            builder = builder.WithNetwork(Network).WithNetworkAliases(NetworkAlias);
        }

        _container = builder.Build();
        await _container.StartAsync();
        Client = new MongoClient(ConnectionString);

        // The tests assert on the replica set the AppHost runs; fail loudly if the container is not one (e.g. a command override dropped --replSet).
        var status = await Client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("replSetGetStatus", 1));
        if (status["myState"].ToInt32() != 1)
        {
            throw new InvalidOperationException("The MongoDB test container is not the PRIMARY of a replica set.");
        }

        await RunUsersScriptAsync();
    }

    /// <summary>
    /// A connection string for another container on <see cref="Network"/> (Debezium): the server by its alias, without credentials (Debezium
    /// takes the user and password as <c>mongodb.user</c> / <c>mongodb.password</c>, as the AppHost and the chart pass them), straight to the
    /// one member (the set is initiated as 127.0.0.1, which only resolves inside the MongoDB container).
    /// </summary>
    public string NetworkConnectionString =>
        $"mongodb://{NetworkAlias}:{ContainerPort}/?authSource=admin&replicaSet={ReplicaSetName}&directConnection=true";

    /// <summary>
    /// A new client on this server signed in as another user of the <c>admin</c> database (e.g. <c>debezium</c>), on connections of its own (a
    /// unique application name, so the driver never hands it connections authenticated earlier). The caller disposes it.
    /// </summary>
    public MongoClient ClientAs(string user, string password)
    {
        var url = new MongoUrlBuilder(ConnectionString)
        {
            Username = user, Password = password, AuthenticationSource = "admin", ApplicationName = $"test-{user}-{Guid.NewGuid():N}",
        };
        return new MongoClient(url.ToMongoUrl());
    }

    /// <summary>
    /// Runs a mongosh script inside the container as the root user, the way someone with database access changes data outside the app.
    /// Fails with mongosh's output when it exits with an error.
    /// </summary>
    public async Task<string> MongoshAsync(string database, string script)
    {
        var url = new MongoUrl(ConnectionString);
        var result = await _container!.ExecAsync(
            ["mongosh", "--quiet", "--username", url.Username, "--password", url.Password, "--authenticationDatabase", "admin", database, "--eval", script]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"mongosh failed ({result.ExitCode}): {result.Stderr} {result.Stdout}");
        }

        return result.Stdout;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
