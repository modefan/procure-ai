using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Application.Cdc;

/// <summary>
/// A change as Debezium Server publishes it to the <c>cdc</c> exchange (JSON without schemas, routing key
/// <c>&lt;database&gt;.public.&lt;table&gt;</c>): the operation (<c>c</c>, <c>u</c>, <c>d</c>, <c>r</c>), the row before and
/// after, and where and when it was committed.
/// </summary>
public sealed record DebeziumChange(
    [property: JsonPropertyName("op")] string Operation,
    [property: JsonPropertyName("before")] JsonElement? Before,
    [property: JsonPropertyName("after")] JsonElement? After,
    [property: JsonPropertyName("source")] DebeziumSource Source)
{
    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["c"] = "Insert",
        ["u"] = "Update",
        ["d"] = "Delete",
        ["r"] = "Snapshot",
    };

    /// <summary>
    /// A row of <c>audit_entries</c> inserted by the app's interceptor, in the same transaction as the change it describes: Debezium's
    /// <c>source.txId</c> (the transaction that really inserted the row) equals the row's own <c>transaction_id</c>. (An update or
    /// delete of one, or a row inserted by another transaction, is a change like any other: someone tampered with the audit.)
    /// </summary>
    [JsonIgnore]
    public bool IsInterceptorEntry => IsInterceptorInsert && !IsForgedInterceptorEntry;

    /// <summary>An <c>audit_entries</c> insert whose <c>transaction_id</c> is not the transaction that inserted it.</summary>
    [JsonIgnore]
    public bool IsForgedInterceptorEntry =>
        IsInterceptorInsert && !(Present(After) is { } row && row.TryGetProperty("transaction_id", out var claimed)
            && claimed.ValueKind == JsonValueKind.Number && claimed.TryGetInt64(out var id) && id == Source.TransactionId);

    private bool IsInterceptorInsert => Source.Table == InterceptorEntry.SourceTable && Operation == "c" && Row is not null;

    /// <summary>
    /// The table Debezium's heartbeat updates in every captured database (CdcMigrations.HeartbeatTable in BuildingBlocks.Persistence;
    /// plan 08 Task 1). It is in each publication only so an idle database's replication slot moves; nobody changes it.
    /// </summary>
    public const string HeartbeatTable = "debezium_heartbeat";

    /// <summary>A change of Debezium's own heartbeat row: never a data change, never "outside the app".</summary>
    [JsonIgnore]
    public bool IsHeartbeat => Source.Table == HeartbeatTable;

    /// <summary>A change of one row (not a truncate or a logical message, which name no row).</summary>
    [JsonIgnore]
    public bool IsRowChange => Operations.ContainsKey(Operation) && Row is not null && Source.TransactionId is not null && Source.Lsn is not null;

    [JsonIgnore]
    public DateTimeOffset CommittedAt => DateTimeOffset.FromUnixTimeMilliseconds(Source.CommittedAtMs);

    [JsonIgnore]
    public long Lsn => Source.Lsn ?? throw new InvalidOperationException("The change has no LSN.");

    private JsonElement? Row => Present(After) ?? Present(Before);

    /// <summary>The interceptor row this change inserted. Transaction and time come from Debezium's metadata, not from the row's own (forgeable) columns.</summary>
    public InterceptorEntry ToInterceptorEntry()
    {
        var row = Present(After) ?? throw new InvalidOperationException("An audit_entries insert without a row.");
        return new InterceptorEntry(
            Guid.Parse(Required(row, "id")),
            Source.Database,
            Source.TransactionId ?? throw new InvalidOperationException("The audit_entries insert has no transaction ID."),
            Required(row, "table_name"),
            Required(row, "primary_key"),
            Required(row, "user_id"),
            Text(row, "actor_id") ?? Required(row, "user_id"),
            Text(row, "calling_service"),
            Text(row, "tenant_id"),
            Text(row, "correlation_id"),
            Text(row, "changes") ?? "[]",
            CommittedAt);
    }

    /// <summary>The change of a captured row, still pending: who made it is decided by the match.</summary>
    public DataChange ToDataChange(CdcOptions cdc)
    {
        var row = Row ?? throw new InvalidOperationException("A change without a row.");
        var table = Source.Table;
        var key = string.Join(",", cdc.KeyColumnsOf(table).Select(column => Text(row, column)));
        var transactionId = Source.TransactionId ?? throw new InvalidOperationException("The change has no transaction ID.");
        return new DataChange(
            $"{Source.Database}:{Lsn.ToString(CultureInfo.InvariantCulture)}:{table}:{key}",
            Source.Database,
            table,
            key,
            SubjectOf(row, table, key, cdc),
            Text(row, "tenant_id"),
            Operations[Operation],
            transactionId,
            CommittedAt,
            Present(Before)?.GetRawText(),
            Present(After)?.GetRawText(),
            Attribution.Pending);
    }

    /// <summary>A column's value as text: strings as they are, numbers and booleans as written, null when absent or SQL NULL.</summary>
    public static string? Text(JsonElement row, string column) =>
        row.TryGetProperty(column, out var value) ? Text(value) : null;

    public static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    // The request a row belongs to: its request_id column, or its key in a table of requests.
    private static Guid? SubjectOf(JsonElement row, string table, string key, CdcOptions cdc)
    {
        var candidate = Text(row, "request_id") ?? (cdc.RequestTables.Contains(table, StringComparer.Ordinal) ? key : null);
        return Guid.TryParse(candidate, out var id) ? id : null;
    }

    private static string Required(JsonElement row, string column) =>
        Text(row, column) ?? throw new InvalidOperationException($"audit_entries row without {column}.");

    private static JsonElement? Present(JsonElement? row) => row is { ValueKind: JsonValueKind.Object } value ? value : null;
}

/// <summary>Where and when a change was committed.</summary>
/// <param name="Database">The service database (Debezium's <c>source.db</c>).</param>
/// <param name="TransactionId">The Postgres transaction ID (<c>source.txId</c>).</param>
/// <param name="Lsn">The change's position in the database's write-ahead log (<c>source.lsn</c>).</param>
/// <param name="CommittedAtMs">The commit time in Unix milliseconds (<c>source.ts_ms</c>).</param>
public sealed record DebeziumSource(
    [property: JsonPropertyName("db")] string Database,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("txId")] long? TransactionId,
    [property: JsonPropertyName("lsn")] long? Lsn,
    [property: JsonPropertyName("ts_ms")] long CommittedAtMs);

/// <summary>Scheduled when a change is still pending: after the grace period it is decided ("outside the app" if still unmatched).</summary>
public sealed record ResolveAttribution(string ChangeId);
