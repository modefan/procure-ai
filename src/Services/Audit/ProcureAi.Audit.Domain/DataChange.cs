namespace ProcureAi.Audit.Domain;

/// <summary>
/// One change of a captured row of a service database, from Debezium: the full row before and after, when it was committed,
/// and who made it (<see cref="Attribution"/>, decided through <see cref="AttributionMatch"/>).
/// </summary>
/// <param name="Id"><c>{database}:{lsn}:{table}:{key}</c> (MongoDB: <c>{database}:{ts_ms}.{ord}:{collection}:{key}</c>): the change's position in the
/// database's log, so a redelivery (or a Debezium restart) stores nothing new.</param>
/// <param name="Database">The service database (Postgres or MongoDB).</param>
/// <param name="Table">The table, or the MongoDB collection.</param>
/// <param name="PrimaryKey">The row's key values, joined by a comma in key order (as the interceptor writes it); a MongoDB document's <c>_id</c>.</param>
/// <param name="SubjectId">The purchase request the row belongs to, when its key or a <c>request_id</c> column holds one.</param>
/// <param name="TenantId">The row's <c>tenant_id</c>; for tables without one, the tenant of the matched interceptor row (null otherwise).</param>
/// <param name="Operation"><c>Insert</c>, <c>Update</c>, <c>Delete</c> or <c>Snapshot</c>.</param>
/// <param name="TransactionId">The Postgres transaction ID (Debezium's <c>source.txId</c>); null for a MongoDB change (attributed by its stamp instead).</param>
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
    long? TransactionId,
    DateTimeOffset CommittedAt,
    string? Before,
    string? After,
    Attribution Attribution)
{
    /// <summary>The stored version (optimistic concurrency: a pending change is decided by the match or the grace timeout, never both).</summary>
    public Guid Version { get; set; }

    public bool IsPending => Attribution.Kind == AttributionKind.Pending;

    /// <summary>
    /// A MongoDB change only: the app's <c>_audit</c> stamp the document has after the change (written by it, or left from an earlier
    /// write); null for Postgres, a delete and a document without one. The next change of the same document is compared with it (a copied
    /// stamp is not new) and a delete is attributed by it (<see cref="DocumentStamp.Deleting"/>).
    /// </summary>
    public DocumentStamp? Stamp { get; init; }

    /// <summary>Made by the app: the user of the interceptor row, and its tenant when the row has no tenant column.</summary>
    public DataChange AttributedTo(InterceptorEntry entry) =>
        this with { Attribution = Attribution.App(entry), TenantId = TenantId ?? entry.TenantId };

    /// <summary>No interceptor row came within the grace period.</summary>
    public DataChange OutsideTheApp() => this with { Attribution = Attribution.OutsideApp };
}
