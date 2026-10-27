namespace ProcureAi.Audit.Domain;

/// <summary>
/// One <c>audit_entries</c> row of a service database, as Debezium delivered it: what the app's audit interceptor recorded in the
/// same transaction as the change (user, actor, calling service, correlation, changed columns). Insert-only; the document ID is
/// the row's ID, so a redelivery stores nothing new.
/// </summary>
/// <param name="Database">The service database (Debezium's topic prefix), e.g. <c>requestsdb</c>.</param>
/// <param name="TransactionId">The Postgres transaction ID (32-bit xid) the row was written in; equals Debezium's <c>source.txId</c>.</param>
/// <param name="Table">The table of the audited change.</param>
/// <param name="PrimaryKey">The changed row's key values, joined by a comma in key order.</param>
/// <param name="Changes">JSON array of <c>{ column, old, new }</c>, sensitive values masked by the interceptor.</param>
/// <param name="OccurredAt">When the app saved the change (shortly before the commit).</param>
public sealed record InterceptorEntry(
    Guid Id,
    string Database,
    long TransactionId,
    string Table,
    string PrimaryKey,
    string UserId,
    string ActorId,
    string? CallingService,
    string? TenantId,
    string? CorrelationId,
    string Changes,
    DateTimeOffset OccurredAt)
{
    /// <summary>The table the audit interceptor writes in every service database.</summary>
    public const string SourceTable = "audit_entries";
}
