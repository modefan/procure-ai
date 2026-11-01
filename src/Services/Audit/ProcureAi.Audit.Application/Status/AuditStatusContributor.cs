using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.Audit.Application.Status;

/// <summary>
/// Audit's part of the status snapshot (plan 06 ruling 10): the open alerts of changes made outside the app (manual SQL, scripts), counted per
/// tenant (<c>outside_app_alerts_open</c>; a change without a tenant counts system-wide), and the newest <see cref="LatestPerTenant"/> of each
/// tenant as alert items that link to the data change log filtered to that table. An alert is withdrawn when the app's own row of the change
/// arrives late; then it is no longer open. The pgaudit summary is a section of its own (<see cref="PgAuditContributor"/>).
/// </summary>
public sealed class AuditStatusContributor(IDataChangeStore changes) : IStatusContributor
{
    public const string SectionName = "audit";

    public const string AlertKind = "outside-app-change";

    public const int LatestPerTenant = 20;

    public string Name => SectionName;

    public async Task<StatusSection> CollectAsync(CancellationToken ct)
    {
        var open = await changes.OpenOutsideAppAlertsAsync(LatestPerTenant, ct);
        var counters = open.Counts.ToDictionary(c => StatusCounters.Key(c.TenantId, StatusCounters.OutsideAppAlertsOpen), c => c.Count);
        var items = open.Latest
            .Select(a => new StatusItem(
                AlertKind,
                Text(a),
                a.TenantId,
                a.CommittedAt,
                LinkFor(a.Table)))
            .ToList();
        return new StatusSection(SectionName, counters, items);
    }

    /// <summary>
    /// What the alert says: table, database, key and operation, never a value of the row (plan 07 Task 0a, plan 06 carry-over 10). An alert
    /// without a tenant is listed for every tenant's admin; the values stay in the data change log, which is tenant-scoped.
    /// </summary>
    public static string Text(OutsideAppAlert alert) =>
        $"{alert.Table} ({alert.Database}) changed outside the app, row {alert.PrimaryKey}"
        + (string.IsNullOrEmpty(alert.Operation) ? "" : $" ({alert.Operation.ToLowerInvariant()})");

    /// <summary>The data change log (screen 24), outside-the-app changes of one table.</summary>
    public static string LinkFor(string table) => "/app/admin/data-changes?outside=true&table=" + Uri.EscapeDataString(table);
}
