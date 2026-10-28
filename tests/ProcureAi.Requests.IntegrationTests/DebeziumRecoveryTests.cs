using DotNet.Testcontainers.Builders;
using Npgsql;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Debezium keeps its offsets (the last WAL position read) in a volume, while the replication slot lives in the database.
/// When the database is reset but the offsets survive (a recreated or emptied Postgres, a removed data volume), the stored
/// position no longer exists on the server. Debezium must then start streaming from a new slot instead of failing to start:
/// a failed Debezium is never healthy, and every API waits for it, so the whole system hung at startup.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class DebeziumRecoveryTests(InfraFixture infra)
{
    private const string Database = "cdc_recovery";
    private const string Slot = "debezium_" + Database;
    private const string RoutingKey = Database + ".public.audit_entries";
    private static readonly TimeSpan CdcTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OffsetFlushTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Debezium_starts_streaming_again_when_its_stored_offset_is_gone_from_the_database()
    {
        var connectionString = await infra.CreateDatabaseAsync(Database);
        var volume = new VolumeBuilder().WithName("procureai-test-debezium-offsets-" + Guid.NewGuid().ToString("N")).Build();
        await volume.CreateAsync();
        try
        {
            await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);

            // First run: capture one change and wait until its offset is flushed to the volume.
            await using (var first = infra.NewDebezium(volume.Name))
            {
                await first.StartAsync(Database, "audit_entries");
                var before = await InsertAuditRowAsync(connectionString);
                await cdc.WaitForAsync(m => m.RoutingKey == RoutingKey && m.After.GetProperty("id").GetString() == before, CdcTimeout);
                await WaitUntilAsync(first.HasStoredOffsetsAsync, OffsetFlushTimeout, "Debezium did not flush its offset");
            }

            // The database loses the position the offsets point to (as a reset database does): the slot is gone.
            await ExecuteAsync(connectionString, $"SELECT pg_drop_replication_slot('{Slot}')");

            // Second run on the same offsets: must become healthy (StartAsync fails otherwise) and capture new changes.
            await using var second = infra.NewDebezium(volume.Name);
            await second.StartAsync(Database, "audit_entries");
            var after = await InsertAuditRowAsync(connectionString);
            var message = await cdc.WaitForAsync(
                m => m.RoutingKey == RoutingKey && m.After.GetProperty("id").GetString() == after, CdcTimeout);
            message.Operation.ShouldBe("c");
        }
        finally
        {
            await volume.DeleteAsync();
        }
    }

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
