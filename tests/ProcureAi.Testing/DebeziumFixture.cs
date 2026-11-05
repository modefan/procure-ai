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

    /// <summary>
    /// A Docker volume for <c>/debezium/data</c> (the offsets), as the AppHost mounts one when it keeps data. Null: the
    /// offsets live and die with the container.
    /// </summary>
    public string? OffsetsVolume { get; init; }

    /// <summary>
    /// Sets <c>DEBEZIUM_SOURCE_SNAPSHOT_MODE_CONFIGURATION_BASED_SNAPSHOT_ON_DATA_ERROR</c> as the AppHost does from
    /// <c>Debezium:SelfHealLostOffsets</c>: a lost offset is dropped instead of failing the start. False is the
    /// application.properties default (a lost offset fails loudly).
    /// </summary>
    public bool SelfHealLostOffsets { get; init; }

    /// <summary>How long <see cref="StartAsync"/> waits for Debezium to report healthy before it fails.</summary>
    public TimeSpan HealthyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Starts Debezium for <paramref name="database"/> and returns when its replication slot is active, so no later change is missed.
    /// Fails (instead of waiting forever) when Debezium does not report healthy within <see cref="HealthyTimeout"/>.
    /// </summary>
    public async Task StartAsync(string database, params string[] tables)
    {
        var limits = TestLimits.Section("Debezium");
        var slot = "debezium_" + database;
        // As deployed: the database's own replication role (<service>_cdc from roles.sql), or the fixture's test role for other databases.
        var (user, password) = PostgresFixture.CdcLoginFor(database);
        var builder = new ContainerBuilder(Image);
        if (OffsetsVolume is not null)
        {
            builder = builder.WithVolumeMount(OffsetsVolume, "/debezium/data");
        }

        _container = builder
            .WithNetwork(network)
            .WithBindMount(
                Path.Combine(PostgresFixture.RepoRoot(), "infra", "debezium", "application.properties"),
                "/debezium/config/application.properties",
                AccessMode.ReadOnly)
            .WithEnvironment("JAVA_OPTS", limits["JavaOpts"])
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_USER", user)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", password)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_DBNAME", database)
            .WithEnvironment("DEBEZIUM_SOURCE_TOPIC_PREFIX", database)
            .WithEnvironment("DEBEZIUM_SOURCE_SLOT_NAME", slot)
            .WithEnvironment("DEBEZIUM_SOURCE_SNAPSHOT_MODE_CONFIGURATION_BASED_SNAPSHOT_ON_DATA_ERROR", SelfHealLostOffsets ? "true" : "false")
            .WithEnvironment("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", string.Join(',', tables.Select(t => "public." + t)))
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", RabbitMqBuilder.DefaultUsername)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", RabbitMqBuilder.DefaultPassword)
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Debezium"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPath("/q/health").ForPort(HttpPort), w => w.WithTimeout(HealthyTimeout)))
            .Build();
        await _container.StartAsync();
        await WaitForReplicationSlotAsync(database, slot);
    }

    /// <summary>True once Debezium has flushed an offset to <c>/debezium/data/offsets.dat</c> (it does so every 5 s after a captured change).</summary>
    public async Task<bool> HasStoredOffsetsAsync()
    {
        var result = await _container!.ExecAsync(["sh", "-c", "test -s /debezium/data/offsets.dat"]);
        return result.ExitCode == 0;
    }

    /// <summary>Stops and removes the container; a mounted <see cref="OffsetsVolume"/> keeps the offsets.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
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
