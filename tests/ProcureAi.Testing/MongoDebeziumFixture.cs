using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.RabbitMq;

namespace ProcureAi.Testing;

/// <summary>
/// The MongoDB Debezium Server (plan 06 ruling 16), started the way the AppHost's <c>AddDebeziumForMongo</c> does it:
/// infra/debezium/application-mongo.properties plus environment variables (connection string, captured collections), with the caps of
/// the <c>Debezium</c> section of tests/testcontainers.settings.json. MongoDB and RabbitMQ (with the <c>cdc</c> exchange) must be reachable
/// on <paramref name="network"/> as <c>mongo</c> and <c>rabbitmq</c>.
/// </summary>
public sealed class MongoDebeziumFixture(INetwork network, MongoFixture mongo) : IAsyncDisposable
{
    private const string Image = "quay.io/debezium/server:3.7.0.Final";
    private const int HttpPort = 8080;
    private static readonly TimeSpan HealthyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StreamingTimeout = TimeSpan.FromSeconds(60);

    private IContainer? _container;

    /// <summary>
    /// Starts Debezium for <paramref name="collections"/> (<c>&lt;database&gt;.&lt;collection&gt;</c>) and returns once its change stream is
    /// open on the server, so no later change is missed (no data snapshot is taken).
    /// </summary>
    public async Task StartAsync(params string[] collections)
    {
        _container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithBindMount(
                Path.Combine(PostgresFixture.RepoRoot(), "infra", "debezium", "application-mongo.properties"),
                "/debezium/config/application.properties",
                AccessMode.ReadOnly)
            .WithEnvironment("JAVA_OPTS", TestLimits.Section("Debezium")["JavaOpts"])
            .WithEnvironment("DEBEZIUM_SOURCE_MONGODB_CONNECTION_STRING", mongo.NetworkConnectionString)
            .WithEnvironment("DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST", string.Join(',', collections))
            .WithEnvironment("DEBEZIUM_SOURCE_SNAPSHOT_MODE_CONFIGURATION_BASED_SNAPSHOT_ON_DATA_ERROR", "false")
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", RabbitMqBuilder.DefaultUsername)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", RabbitMqBuilder.DefaultPassword)
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Debezium"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPath("/q/health").ForPort(HttpPort), w => w.WithTimeout(HealthyTimeout)))
            .Build();
        await _container.StartAsync();
        await WaitForChangeStreamAsync();
    }

    /// <summary>Freezes the Debezium container (<c>docker pause</c>): it reads nothing until <see cref="UnpauseAsync"/>, as when it lags behind.</summary>
    public Task PauseAsync() => _container!.PauseAsync();

    public Task UnpauseAsync() => _container!.UnpauseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    // Debezium reports healthy before its change stream cursor exists; a change made in between would be lost. The server lists the
    // cursor (open or between getMores) in $currentOp.
    private async Task WaitForChangeStreamAsync()
    {
        var deadline = DateTime.UtcNow + StreamingTimeout;
        var pipeline = new[]
        {
            new BsonDocument("$currentOp", new BsonDocument { ["allUsers"] = true, ["idleCursors"] = true }),
        };
        while (true)
        {
            var operations = await mongo.Client.GetDatabase("admin")
                .AggregateAsync<BsonDocument>(pipeline);
            if ((await operations.ToListAsync()).Any(op => op.ToJson().Contains("$changeStream", StringComparison.Ordinal)))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Debezium did not open its MongoDB change stream within {StreamingTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
