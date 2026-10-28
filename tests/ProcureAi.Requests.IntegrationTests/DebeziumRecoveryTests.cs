using DotNet.Testcontainers.Builders;
using Npgsql;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Debezium keeps its offsets (the last WAL position read) in a volume, while the replication slot lives in the database.
/// When the database is reset but the offsets survive (a recreated or emptied Postgres, a removed data volume, a dropped
/// slot), the stored position no longer exists on the server. By default (infra/debezium/application.properties) Debezium
/// then fails loudly. With the self-heal switch (set by the AppHost for local runs from <c>Debezium:SelfHealLostOffsets</c>)
/// it drops the offset and streams from a new slot instead, accepting a CDC gap.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class DebeziumRecoveryTests(InfraFixture infra)
{
    private static readonly TimeSpan CdcTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OffsetFlushTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task With_self_heal_debezium_starts_streaming_again_when_its_stored_offset_is_gone_from_the_database()
    {
        const string database = "cdc_recovery_heal";
        await WithLostOffsetAsync(database, async (volume, connectionString, cdc) =>
        {
            // Second run on the same offsets: must become healthy (StartAsync fails otherwise) and capture new changes.
            await using var second = infra.NewDebezium(volume, selfHealLostOffsets: true);
            await second.StartAsync(database, "audit_entries");
            var after = await InsertAuditRowAsync(connectionString);
            var message = await cdc.WaitForAsync(
                m => m.RoutingKey == RoutingKey(database) && m.After.GetProperty("id").GetString() == after, CdcTimeout);
            message.Operation.ShouldBe("c");
        });
    }

    [Fact]
    public async Task Without_self_heal_debezium_refuses_to_start_when_its_stored_offset_is_gone_from_the_database()
    {
        const string database = "cdc_recovery_strict";
        await WithLostOffsetAsync(database, async (volume, _, _) =>
        {
            // The default: a lost position must be noticed (a dropped or renamed slot in a deployment), not silently skipped.
            await using var second = infra.NewDebezium(volume, selfHealLostOffsets: false, healthyTimeout: TimeSpan.FromSeconds(60));
            await Should.ThrowAsync<TimeoutException>(() => second.StartAsync(database, "audit_entries"));
        });
    }

    /// <summary>
    /// Runs Debezium once on a fresh offsets volume until a captured change's offset is stored, then drops the replication
    /// slot (what a reset database leaves behind) and hands the volume to <paramref name="secondRun"/>.
    /// </summary>
    private async Task WithLostOffsetAsync(string database, Func<string, string, CdcListener, Task> secondRun)
    {
        var connectionString = await infra.CreateDatabaseAsync(database);
        var volume = new VolumeBuilder().WithName("procureai-test-debezium-offsets-" + Guid.NewGuid().ToString("N")).Build();
        await volume.CreateAsync();
        try
        {
            await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);

            await using (var first = infra.NewDebezium(volume.Name, selfHealLostOffsets: false))
            {
                await first.StartAsync(database, "audit_entries");
                var before = await InsertAuditRowAsync(connectionString);
                await cdc.WaitForAsync(
                    m => m.RoutingKey == RoutingKey(database) && m.After.GetProperty("id").GetString() == before, CdcTimeout);
                await WaitUntilAsync(first.HasStoredOffsetsAsync, OffsetFlushTimeout, "Debezium did not flush its offset");
            }

            await DropSlotAsync(connectionString, database);
            await secondRun(volume.Name, connectionString, cdc);
        }
        finally
        {
            // An unused slot keeps Postgres from recycling WAL for the rest of the run.
            await DropSlotAsync(connectionString, database);
            await volume.DeleteAsync();
        }
    }

    private static string RoutingKey(string database) => database + ".public.audit_entries";

    private static Task DropSlotAsync(string connectionString, string database) =>
        ExecuteAsync(
            connectionString,
            "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots " +
            $"WHERE slot_name = 'debezium_{database}' AND NOT active");

    private static async Task<string> InsertAuditRowAsync(string connectionString)
    {
        var id = Guid.NewGuid().ToString();
        await ExecuteAsync(
            connectionString,
            "INSERT INTO audit_entries (id, table_name, primary_key, operation, changes, occurred_at, transaction_id, actor_id, user_id) " +
            $"VALUES ('{id}', 'debezium_recovery', 'x', 'Insert', '{{}}'::jsonb, now(), txid_current(), 'test', 'test')");
        return id;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
