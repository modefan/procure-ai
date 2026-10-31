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
/// Not deleted: the interceptor's own <c>audit_entries</c> (a delete of one would be a captured change without an interceptor row of its
/// own, so an outside-the-app alert) and Wolverine's tables (cleared through its message store). The per-tenant counters
/// (<c>tenant_sequences</c>, not captured) are emptied with one statement, so request and order numbers start again at the first one.
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
