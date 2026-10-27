using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using ProcureAi.Contracts;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// One made-up transaction of a made-up database, as Debezium Server publishes it to <c>cdc</c> (JSON without schemas): an update
/// of one <c>purchase_requests</c> row and, when the app made it, the <c>audit_entries</c> row its interceptor wrote in the same
/// transaction. Lets a test decide what arrives, in which order, and how often.
/// </summary>
public sealed record SyntheticTransaction(
    string Database, string Tenant, string UserId, string ActorId, long TransactionId, long ChangeLsn, long EntryLsn, Guid EntryId,
    Guid RowId, DateTimeOffset CommittedAt, string Table = "purchase_requests", bool HasTenantColumn = true)
{
    private static long _next = 1_000_000;

    public static SyntheticTransaction New(string database, string tenant, string user = DemoUsers.Tom, string actor = DemoUsers.Tom)
    {
        var tx = Interlocked.Add(ref _next, 10);
        return new SyntheticTransaction(
            database, tenant, user, actor, tx, tx * 100, tx * 100 + 1, Guid.CreateVersion7(), Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    /// <summary>The document ID Audit gives the change: database, LSN, table and key.</summary>
    public string ChangeId => $"{Database}:{ChangeLsn.ToString(CultureInfo.InvariantCulture)}:{Table}:{RowId}";

    public string ChangeRoutingKey => $"{Database}.public.{Table}";

    public string EntryRoutingKey => $"{Database}.public.audit_entries";

    /// <summary>The row update (op <c>u</c>), with the full before image (REPLICA IDENTITY FULL).</summary>
    public string ChangeJson()
    {
        object Row(string title) => HasTenantColumn
            ? new Dictionary<string, object?> { ["id"] = RowId, ["tenant_id"] = Tenant, ["title"] = title }
            : new Dictionary<string, object?> { ["id"] = RowId, ["title"] = title };
        return Envelope(Table, "u", ChangeLsn, Row("Old title"), Row("New title"));
    }

    /// <summary>The interceptor's row (op <c>c</c> on <c>audit_entries</c>), written in the same transaction.</summary>
    public string EntryJson() => Envelope("audit_entries", "c", EntryLsn, before: null, after: EntryRow());

    private Dictionary<string, object?> EntryRow() =>
        new Dictionary<string, object?>
        {
            ["id"] = EntryId,
            ["tenant_id"] = Tenant,
            ["user_id"] = UserId,
            ["actor_id"] = ActorId,
            ["calling_service"] = null,
            ["correlation_id"] = "corr-" + TransactionId.ToString(CultureInfo.InvariantCulture),
            ["transaction_id"] = TransactionId,
            ["table_name"] = Table,
            ["primary_key"] = RowId.ToString(),
            ["operation"] = "Update",
            ["changes"] = """[{"column":"title","old":"Old title","new":"New title"}]""",
            // Debezium's ZonedTimestamp: ISO 8601 text in UTC.
            ["occurred_at"] = CommittedAt.AddMilliseconds(-5).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// A forged copy of the interceptor's row: same <c>transaction_id</c> column, but inserted by a transaction of its own
    /// (Debezium's <c>source.txId</c> is <paramref name="actualTransactionId"/>).
    /// </summary>
    public string ForgedEntryJson(long actualTransactionId) => Envelope("audit_entries", "c", EntryLsn, null, EntryRow(), actualTransactionId);

    private string Envelope(string table, string op, long lsn, object? before, object? after, long? sourceTransactionId = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["before"] = before,
            ["after"] = after,
            ["source"] = new Dictionary<string, object?>
            {
                ["version"] = "3.7.0.Final",
                ["connector"] = "postgresql",
                ["name"] = Database,
                ["ts_ms"] = CommittedAt.ToUnixTimeMilliseconds(),
                ["snapshot"] = "false",
                ["db"] = Database,
                ["sequence"] = $"[null,\"{lsn}\"]",
                ["schema"] = "public",
                ["table"] = table,
                ["txId"] = sourceTransactionId ?? TransactionId,
                ["lsn"] = lsn,
                ["xmin"] = null,
            },
            ["op"] = op,
            ["ts_ms"] = CommittedAt.ToUnixTimeMilliseconds() + 3,
        });
}

/// <summary>Records what a counter of the process's meters measured, by instrument name (MeterListener sees every meter of every host).</summary>
public sealed class CounterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(long Value, Dictionary<string, string?> Tags)> _measurements = new();

    public CounterRecorder(string instrument)
    {
        _listener.InstrumentPublished = (published, listener) =>
        {
            if (published.Name == instrument)
            {
                listener.EnableMeasurementEvents(published);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            _measurements.Enqueue((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
        _listener.Start();
    }

    /// <summary>The sum measured with these tag values.</summary>
    public long Sum(params (string Key, string Value)[] tags) =>
        _measurements.Where(m => tags.All(t => m.Tags.GetValueOrDefault(t.Key) == t.Value)).Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
