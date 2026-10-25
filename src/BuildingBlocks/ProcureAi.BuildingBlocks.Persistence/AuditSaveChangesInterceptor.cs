using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Adds one <see cref="AuditEntry"/> per changed entity to the same <c>SaveChanges</c>, so the audit rows and the
/// changes commit or roll back together, also inside a transaction Wolverine opened around a handler.
/// Limit: keys must be known before saving (client-generated, as all our entities use); a database-generated key
/// would still be a temporary value here.
/// </summary>
public sealed class AuditSaveChangesInterceptor(IAuditContext? auditContext, TimeProvider timeProvider) : SaveChangesInterceptor
{
    public const string MaskedValue = "***";
    public const string DefaultUser = "system";

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
            .Where(e => e.Metadata.ClrType.GetCustomAttribute<AuditExcludeAttribute>() is null)
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var correlationId = CurrentCorrelationId();
        foreach (var entry in changed)
        {
            var table = entry.Metadata.GetTableName();
            if (table is null)
            {
                continue;
            }

            var changes = ChangesOf(entry);
            if (changes.Count == 0)
            {
                continue;
            }

            context.Set<AuditEntry>().Add(new AuditEntry
            {
                Id = Guid.CreateVersion7(),
                TenantId = auditContext?.TenantId,
                UserId = auditContext?.UserId ?? DefaultUser,
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
            if (entry.State == EntityState.Modified && Equals(oldValue, newValue))
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
