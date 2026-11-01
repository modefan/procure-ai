namespace ProcureAi.Audit.Domain;

/// <summary>
/// A change made outside the app, for the system status screen. The ID is the change's ID (one alert per change). Withdrawn
/// (not deleted) when the interceptor row of the change still arrives after the grace period: the change was the app's after all.
/// It names the row (table, key, operation) and never holds a value of it: an alert without a tenant is listed for every tenant's admin.
/// </summary>
/// <param name="Operation">The change's operation (<c>Insert</c>, <c>Update</c>, <c>Delete</c>); null on alerts stored before it was kept.</param>
public sealed record OutsideAppAlert(
    string Id,
    string Database,
    string Table,
    string PrimaryKey,
    string? TenantId,
    long? TransactionId,
    DateTimeOffset CommittedAt,
    DateTimeOffset RaisedAt,
    DateTimeOffset? WithdrawnAt,
    string? Operation = null)
{
    public static OutsideAppAlert For(DataChange change, DateTimeOffset now) =>
        new(change.Id, change.Database, change.Table, change.PrimaryKey, change.TenantId, change.TransactionId, change.CommittedAt, now, null, change.Operation);

    public OutsideAppAlert Withdrawn(DateTimeOffset now) => WithdrawnAt is null ? this with { WithdrawnAt = now } : this;
}
