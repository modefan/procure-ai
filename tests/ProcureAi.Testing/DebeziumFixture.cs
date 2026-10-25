using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.RabbitMq;

namespace ProcureAi.Testing;

/// <summary>
/// One Debezium Server for one database, started the way the AppHost's <c>AddDebeziumFor</c> does it: the shared
/// infra/debezium/application.properties plus per-database environment variables, with the memory and heap caps of
/// the <c>Debezium</c> section of tests/testcontainers.settings.json. The database must already have its publication
/// (its migration created it) and the Postgres, RabbitMQ and exchange must be reachable on <paramref name="network"/>
/// as <c>postgres</c> and <c>rabbitmq</c>.
/// </summary>
public sealed class DebeziumFixture(INetwork network, PostgresFixture postgres) : IAsyncDisposable
{
    private const string Image = "quay.io/debezium/server:3.7.0.Final";
    private const int HttpPort = 8080;
    private static readonly TimeSpan StreamingTimeout = TimeSpan.FromSeconds(60);

    private IContainer? _container;

    /// <summary>Starts Debezium for <paramref name="database"/> and returns when its replication slot is active, so no later change is missed.</summary>
    public async Task StartAsync(string database, params string[] tables)
    {
        var limits = TestLimits.Section("Debezium");
        var slot = "debezium_" + database;
        _container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithBindMount(
                Path.Combine(PostgresFixture.RepoRoot(), "infra", "debezium", "application.properties"),
                "/debezium/config/application.properties",
                AccessMode.ReadOnly)
            .WithEnvironment("JAVA_OPTS", limits["JavaOpts"])
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", PostgresFixture.DebeziumTestPassword)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_DBNAME", database)
            .WithEnvironment("DEBEZIUM_SOURCE_TOPIC_PREFIX", database)
            .WithEnvironment("DEBEZIUM_SOURCE_SLOT_NAME", slot)
            .WithEnvironment("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", string.Join(',', tables.Select(t => "public." + t)))
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", RabbitMqBuilder.DefaultUsername)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", RabbitMqBuilder.DefaultPassword)
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Debezium"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/q/health").ForPort(HttpPort)))
            .Build();
        await _container.StartAsync();
        await WaitForReplicationSlotAsync(database, slot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task WaitForReplicationSlotAsync(string database, string slot)
    {
        var deadline = DateTime.UtcNow + StreamingTimeout;
        while (true)
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_replication_slots WHERE slot_name = @slot AND active", connection);
            command.Parameters.AddWithValue("slot", slot);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Debezium did not start streaming {database} within {StreamingTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
