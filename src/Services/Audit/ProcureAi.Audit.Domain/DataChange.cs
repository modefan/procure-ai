namespace ProcureAi.Audit.Domain;

/// <summary>
/// One change of a captured row of a service database, from Debezium: the full row before and after, when it was committed,
/// and who made it (<see cref="Attribution"/>, decided through <see cref="AttributionMatch"/>).
/// </summary>
/// <param name="Id"><c>{database}:{lsn}:{table}:{key}</c>: the change's position in the database's log, so a redelivery (or a Debezium restart) stores nothing new.</param>
/// <param name="PrimaryKey">The row's key values, joined by a comma in key order (as the interceptor writes it).</param>
/// <param name="SubjectId">The purchase request the row belongs to, when its key or a <c>request_id</c> column holds one.</param>
/// <param name="TenantId">The row's <c>tenant_id</c>; for tables without one, the tenant of the matched interceptor row (null otherwise).</param>
/// <param name="Operation"><c>Insert</c>, <c>Update</c>, <c>Delete</c> or <c>Snapshot</c>.</param>
/// <param name="TransactionId">The Postgres transaction ID (Debezium's <c>source.txId</c>).</param>
/// <param name="CommittedAt">When the transaction committed (Debezium's <c>source.ts_ms</c>).</param>
/// <param name="Before">The row before the change as JSON (null for an insert).</param>
/// <param name="After">The row after the change as JSON (null for a delete).</param>
public sealed record DataChange(
    string Id,
    string Database,
    string Table,
    string PrimaryKey,
    Guid? SubjectId,
    string? TenantId,
    string Operation,
    long TransactionId,
    DateTimeOffset CommittedAt,
    string? Before,
    string? After,
    Attribution Attribution)
{
    /// <summary>The stored version (optimistic concurrency: a pending change is decided by the match or the grace timeout, never both).</summary>
    public Guid Version { get; set; }

    public bool IsPending => Attribution.Kind == AttributionKind.Pending;

    /// <summary>Made by the app: the user of the interceptor row, and its tenant when the row has no tenant column.</summary>
    public DataChange AttributedTo(InterceptorEntry entry) =>
        this with { Attribution = Attribution.App(entry), TenantId = TenantId ?? entry.TenantId };

    /// <summary>No interceptor row came within the grace period.</summary>
    public DataChange OutsideTheApp() => this with { Attribution = Attribution.OutsideApp };
}
