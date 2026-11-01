using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// The EF Core half of a service's demo reset (plan 06 rulings 4 and 5): deletes every row of the service's own entity types, of every
/// tenant, with ordinary tracked deletes in one <c>SaveChanges</c>. The audit interceptor therefore writes one <c>audit_entries</c> row per
/// deleted row in the same transaction, so Audit matches each Debezium delete to the app (never "changed outside the app"). EF orders the
/// deletes by the foreign keys; owned types go with their owner.
/// <para>
/// Not deleted here: the interceptor's own <c>audit_entries</c> (a tracked delete of one would be a captured change without an interceptor
/// row of its own, so an outside-the-app alert; <see cref="ClearAuditEntriesAsync"/> empties them instead, first) and Wolverine's tables
/// (cleared through its message store). The per-tenant counters (<c>tenant_sequences</c>, not captured) are emptied with one statement, so
/// request and order numbers start again at the first one.
/// </para>
/// </summary>
public static class EfDemoReset
{
    private static readonly MethodInfo LoadAllMethod =
        typeof(EfDemoReset).GetMethod(nameof(LoadAllAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Deletes the rows (none kept unless <paramref name="keep"/> says so) and saves. Runs inside the caller's transaction when there is
    /// one. Returns the number of entities deleted.
    /// </summary>
    public static async Task<int> DeleteAllRowsAsync(DbContext db, Func<object, bool>? keep = null, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var entityType in ResettableTypes(db.Model))
        {
            var load = (Task<List<object>>)LoadAllMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [db, cancellationToken])!;
            foreach (var entity in await load)
            {
                if (keep?.Invoke(entity) == true)
                {
                    continue;
                }

                db.Remove(entity);
                deleted++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        if (db.Model.FindEntityType(typeof(TenantSequenceRow)) is not null)
        {
            await db.Set<TenantSequenceRow>().ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }

    /// <summary>The interceptor's table, as <see cref="ModelBuilderExtensions.AddAuditEntries"/> maps it.</summary>
    public const string AuditEntriesTable = "audit_entries";

    private const string TruncateAuditEntriesSql = "TRUNCATE TABLE " + AuditEntriesTable;

    /// <summary>
    /// Empties the service's <c>audit_entries</c> (spec 5.1: the reset clears demo audit data too; plan 07 Task 0b, carry-over 5). Called
    /// first, before <see cref="DeleteAllRowsAsync"/>, so the table afterwards holds only the reset's own Delete rows (ruling 5) and what came
    /// after; Audit's copy of everything is cleared after the grace period anyway. <c>TRUNCATE</c>, not row deletes: Debezium skips truncates
    /// (<c>skipped.operations</c> defaults to <c>t</c>) and Audit ignores a change without a row, so no captured delete of an interceptor row
    /// ever looks like tampering. Its own statement, outside the reset's transaction, so the table's exclusive lock is held only for the
    /// truncate. Returns false when the model has no audit table.
    /// </summary>
    public static async Task<bool> ClearAuditEntriesAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Model.FindEntityType(typeof(AuditEntry))?.GetTableName() != AuditEntriesTable)
        {
            return false;
        }

        await db.Database.ExecuteSqlRawAsync(TruncateAuditEntriesSql, cancellationToken);
        return true;
    }

    /// <summary>The root entity types a reset deletes: the service's own tables, no owned or derived types, no audit rows, no Wolverine tables.</summary>
    public static IReadOnlyList<IEntityType> ResettableTypes(IModel model) =>
    [
        .. model.GetEntityTypes().Where(t =>
            !t.IsOwned()
            && t.BaseType is null
            && !t.HasSharedClrType
            && t.FindPrimaryKey() is not null
            && t.GetTableName() is { } table
            && t.ClrType != typeof(AuditEntry)
            && t.ClrType != typeof(TenantSequenceRow)
            && !table.StartsWith("wolverine_", StringComparison.Ordinal)
            && t.ClrType.Assembly.GetName().Name?.StartsWith("Wolverine", StringComparison.Ordinal) != true),
    ];

    private static async Task<List<object>> LoadAllAsync<T>(DbContext db, CancellationToken cancellationToken)
        where T : class =>
        [.. await db.Set<T>().IgnoreQueryFilters().ToListAsync(cancellationToken)];
}
