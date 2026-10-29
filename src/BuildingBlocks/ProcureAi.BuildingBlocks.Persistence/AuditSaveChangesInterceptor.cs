using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Adds one <see cref="AuditEntry"/> per changed entity to the same <c>SaveChanges</c>, so the audit rows and the
/// changes commit or roll back together, also inside a transaction Wolverine opened around a handler.
/// The interceptor is stateless and safe as a singleton: the caller comes from the saving DbContext
/// (<see cref="IAuditContextSource"/>), which lives in the request's scope.
/// <para>
/// Not supported: database-generated keys (a temporary value at this point; all our entities use client-generated
/// keys) and owned/complex-type properties stored as JSON or inside the owner's table without their own entry.
/// <see cref="AuditEntry.TransactionId"/> is the 32-bit xid and wraps around after about 4 billion transactions, so a
/// consumer (the Audit service, plan 02) should match <c>txId</c> together with a time or LSN window, not alone.
/// </para>
/// </summary>
public sealed class AuditSaveChangesInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public const string MaskedValue = "***";

    /// <summary>Tag the correlation ID middleware puts on the request activity.</summary>
    public const string CorrelationTag = "correlation.id";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditEntries(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddAuditEntries(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void AddAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changed = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => !IsInternal(e.Metadata))
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var caller = (context as IAuditContextSource)?.Caller;
        var now = timeProvider.GetUtcNow();
        var correlationId = CurrentCorrelationId();
        foreach (var entry in changed)
        {
            var table = entry.Metadata.GetTableName();
            if (table is null)
            {
                continue;
            }

            // One row for every statement EF sends, also an UPDATE that changes no value (an entity marked Modified as a whole,
            // e.g. a request whose lines changed, so its version moves). Change data capture sees that UPDATE; without a row of
            // the same transaction, Audit would flag it "changed outside the app". Its changes list is then empty.
            var changes = ChangesOf(entry);
            if (changes.Count == 0 && !SendsStatement(entry))
            {
                continue;
            }

            context.Set<AuditEntry>().Add(new AuditEntry
            {
                Id = Guid.CreateVersion7(),
                TenantId = caller?.TenantId,
                UserId = caller?.UserId ?? SystemCaller.Id,
                ActorId = caller?.ActorId ?? SystemCaller.Id,
                CallingService = caller?.CallingService,
                CorrelationId = correlationId,
                Table = table,
                PrimaryKey = PrimaryKeyOf(entry),
                Operation = entry.State switch
                {
                    EntityState.Added => AuditOperation.Insert,
                    EntityState.Deleted => AuditOperation.Delete,
                    _ => AuditOperation.Update,
                },
                Changes = JsonSerializer.Serialize(changes, JsonOptions),
                OccurredAt = now,
            });
        }
    }

    /// <summary>
    /// Internal tables are not data a person changed, so they get no audit row (CLAUDE.md "Audit": internal tables are not
    /// captured): entity types marked <see cref="AuditExcludeAttribute"/> (the audit rows themselves, the webhook inbox) or excluded in the model
    /// (<see cref="AuditExclusion.ExcludeFromAudit{T}"/>), and
    /// Wolverine's own entities, which its EF Core integration maps into every service's DbContext (the outbox and inbox envelope
    /// tables, <c>wolverine_*</c>, whose rows carry whole message bodies and come and go with every message).
    /// </summary>
    private static bool IsInternal(IEntityType entityType) =>
        entityType.ClrType.GetCustomAttribute<AuditExcludeAttribute>() is not null
        || entityType.FindAnnotation(AuditExclusion.Annotation)?.Value is true
        || entityType.ClrType.Assembly.GetName().Name?.StartsWith(WolverinePrefix, StringComparison.Ordinal) == true
        || entityType.GetTableName()?.StartsWith(WolverineTablePrefix, StringComparison.Ordinal) == true;

    private const string WolverinePrefix = "Wolverine";

    private const string WolverineTablePrefix = "wolverine_";

    private static List<AuditChange> ChangesOf(EntityEntry entry)
    {
        var changes = new List<AuditChange>();
        foreach (var property in entry.Properties)
        {
            var metadata = property.Metadata;
            var column = metadata.GetColumnName();
            // Columns the database maintains itself (xmin) change on every write and say nothing about the data.
            var databaseMaintained = metadata.IsConcurrencyToken && metadata.ValueGenerated != ValueGenerated.Never;
            if (column is null || databaseMaintained)
            {
                continue;
            }

            var oldValue = entry.State == EntityState.Added ? null : Provider(metadata, property.OriginalValue);
            var newValue = entry.State == EntityState.Deleted ? null : Provider(metadata, property.CurrentValue);
            if (entry.State == EntityState.Modified && SameValue(oldValue, newValue))
            {
                continue;
            }

            if (metadata.PropertyInfo?.GetCustomAttribute<SensitiveAttribute>() is not null)
            {
                oldValue = oldValue is null ? null : MaskedValue;
                newValue = newValue is null ? null : MaskedValue;
            }

            changes.Add(new AuditChange(column, oldValue, newValue));
        }

        return changes;
    }

    /// <summary>
    /// Whether EF sends a statement for the entry: always for an insert or delete; for an update when any property is marked
    /// modified, whatever its value (EF writes marked properties without comparing them).
    /// </summary>
    private static bool SendsStatement(EntityEntry entry) =>
        entry.State != EntityState.Modified || entry.Properties.Any(p => p.IsModified);

    // Scalars compare by value; a primitive collection (text[] column) by its items, since the snapshot and the current
    // value are different collection objects that never compare equal by reference.
    private static bool SameValue(object? oldValue, object? newValue) =>
        oldValue is System.Collections.IEnumerable and not string && newValue is System.Collections.IEnumerable and not string
            ? ((System.Collections.IEnumerable)oldValue).Cast<object?>().SequenceEqual(((System.Collections.IEnumerable)newValue).Cast<object?>())
            : Equals(oldValue, newValue);

    // The value as stored (enums as text, value objects as their column value), not the CLR object.
    private static object? Provider(IProperty property, object? value)
    {
        var converter = property.GetTypeMapping().Converter;
        return converter is not null && value is not null ? converter.ConvertToProvider(value) : value;
    }

    private static string PrimaryKeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()!;
        return string.Join(",", key.Properties.Select(p =>
        {
            var property = entry.Property(p.Name);
            var value = entry.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }));
    }

    // The correlation ID middleware tags the request activity; handlers may run in a child activity.
    private static string? CurrentCorrelationId()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (activity.GetTagItem(CorrelationTag) is string tagged)
            {
                return tagged;
            }
        }

        return Activity.Current?.TraceId.ToString();
    }
}
