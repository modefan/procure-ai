using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.Audit.Application.Status;

/// <summary>
/// The pgaudit part of Audit's status snapshot (plan 06 ruling 14): what was done to the databases as which database role, from the
/// Postgres server's audit log. Counters <c>*:pgaudit_&lt;class&gt;</c> (system-wide; the class in lower case, e.g. <c>pgaudit_ddl</c>)
/// and the newest statements as items of kind <see cref="ItemKind"/>. The log has no tenant: everything is system-wide.
/// </summary>
public sealed class PgAuditContributor(IPgAuditReader reader) : IStatusContributor
{
    public const string SectionName = "pgaudit";

    public const string ItemKind = "pgaudit";

    public const string CounterPrefix = "pgaudit_";

    public string Name => SectionName;

    public async Task<StatusSection> CollectAsync(CancellationToken ct)
    {
        var summary = await reader.ReadAsync(ct);
        var counters = summary.Counts
            .GroupBy(c => CounterPrefix + c.Class.ToLowerInvariant())
            .ToDictionary(g => StatusCounters.Key(null, g.Key), g => g.Sum(c => c.Count));
        var items = summary.Latest
            .Select(s => new StatusItem(ItemKind, $"{s.Role} in {s.Database}: {s.Text}", null, s.At, null))
            .ToList();
        return new StatusSection(SectionName, counters, items);
    }
}
