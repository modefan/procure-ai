using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Audit.Application.Abstractions;

/// <summary>What the data-change log can be filtered by. <see cref="TenantId"/> null means every tenant (the platform view).</summary>
public sealed record DataChangeFilter(
    string? TenantId, string? Database, string? Table, string? UserId, bool OutsideOnly, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// The data change audit (Marten in Infrastructure). Every write is idempotent by document ID and decides a change's attribution
/// atomically through its <see cref="AttributionMatch"/>; a write that lost a race to a concurrent one throws a concurrency
/// exception, and the handler runs again (Wolverine's retry policy) and then sees the winner's outcome.
/// </summary>
public interface IDataChangeStore
{
    /// <summary>
    /// Stores a change (once) and matches it to an interceptor row already there. Returns its attribution: still
    /// <see cref="AttributionKind.Pending"/> when no row has arrived yet (also on a redelivery of a pending change).
    /// </summary>
    Task<Attribution> RecordChangeAsync(DataChange change, long lsn, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an interceptor row (once) and attributes the changes already there that it matches, including one already declared
    /// "outside the app" (its alert is withdrawn).
    /// </summary>
    Task RecordInterceptorEntryAsync(InterceptorEntry entry, long lsn, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Declares a still-pending change "outside the app" and raises its alert. Returns the change when it was declared now; null
    /// when it was matched meanwhile (or decided before, or is unknown), so a late timeout never overwrites a match.
    /// </summary>
    Task<DataChange?> ResolveAsync(string changeId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a change whose attribution is already decided (a MongoDB change: <see cref="AttributionKind.App"/> or
    /// <see cref="AttributionKind.OutsideApp"/>), once, with the alert of one made outside the app in the same commit. Returns false when
    /// it was stored before (a redelivery), so the caller counts and logs it once.
    /// </summary>
    Task<bool> RecordDecidedChangeAsync(DataChange change, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The newest stored change of one row or document, other than <paramref name="exceptId"/>; null when there is none.</summary>
    Task<DataChange?> LatestChangeAsync(string database, string table, string primaryKey, string exceptId, CancellationToken cancellationToken);

    /// <summary>Deletes changes, interceptor rows, matches and alerts from before <paramref name="cutoff"/>, at most <paramref name="batchSize"/> per statement. Returns how many.</summary>
    Task<int> PurgeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);

    /// <summary>The changes of one tenant about one purchase request, oldest first.</summary>
    Task<PagedResult<DataChange>> ForSubjectAsync(string tenantId, Guid subjectId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The newest stored change of one tenant's row of <paramref name="database"/>.<paramref name="table"/> about one request; null when there is none.</summary>
    Task<DataChange?> LatestRowOfSubjectAsync(string tenantId, string database, string table, Guid subjectId, CancellationToken cancellationToken);

    /// <summary>The data-change log, newest first.</summary>
    Task<PagedResult<DataChange>> SearchAsync(DataChangeFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// The alerts of changes made outside the app that are still open (not withdrawn), of every tenant: how many per tenant (null: a change
    /// without a tenant), and the newest <paramref name="latestPerTenant"/> of each tenant.
    /// </summary>
    Task<OpenOutsideAppAlerts> OpenOutsideAppAlertsAsync(int latestPerTenant, CancellationToken cancellationToken);
}

/// <param name="Counts">Open alerts per tenant (TenantId null: changes without a tenant).</param>
/// <param name="Latest">The newest open alerts of each tenant, newest first.</param>
public sealed record OpenOutsideAppAlerts(IReadOnlyList<(string? TenantId, long Count)> Counts, IReadOnlyList<OutsideAppAlert> Latest);

/// <summary>The Audit service's business metrics (OpenTelemetry meter in Infrastructure).</summary>
public interface IAuditMetrics
{
    /// <summary><c>procure.audit.outside_app_changes</c> + 1, tagged with the database and table.</summary>
    void OutsideAppChange(string database, string table);
}
