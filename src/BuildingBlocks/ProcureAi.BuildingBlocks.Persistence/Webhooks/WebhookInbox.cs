using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence;

public static class WebhookInbox
{
    internal const string KeyName = "pk_processed_webhooks";
    private const string UniqueViolation = "23505";

    /// <summary>Adds the table <c>processed_webhooks</c> to the model. Never captured by Debezium.</summary>
    public static ModelBuilder AddProcessedWebhooks(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedWebhook>(row =>
        {
            row.ToTable("processed_webhooks");
            row.HasKey(r => new { r.Source, r.EventId }).HasName(KeyName);
            row.Property(r => r.Source).HasColumnName("source").HasMaxLength(100);
            row.Property(r => r.EventId).HasColumnName("event_id").HasMaxLength(200);
            row.Property(r => r.ReceivedAt).HasColumnName("received_at");
        });
        return modelBuilder;
    }

    /// <summary>Registers <see cref="IWebhookInbox"/> on <typeparamref name="TDbContext"/> (which called <see cref="AddProcessedWebhooks"/>).</summary>
    public static IServiceCollection AddWebhookInbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext =>
        services.AddScoped<IWebhookInbox, WebhookInbox<TDbContext>>();

    /// <summary>True when the save failed because the same webhook event was committed concurrently: treat it as already processed.</summary>
    public static bool IsDuplicate(DbUpdateException exception) =>
        exception.Entries.Any(e => e.Entity is ProcessedWebhook)
        && exception.InnerException is DbException { SqlState: UniqueViolation } inner
        && inner.Message.Contains(KeyName, StringComparison.OrdinalIgnoreCase);
}

public sealed class WebhookInbox<TDbContext>(TDbContext db, TimeProvider? clock = null) : IWebhookInbox
    where TDbContext : DbContext
{
    public async Task<bool> TryRecordAsync(string source, string eventId, CancellationToken cancellationToken)
    {
        var set = db.Set<ProcessedWebhook>();
        if (set.Local.Any(r => r.Source == source && r.EventId == eventId)
            || await set.AnyAsync(r => r.Source == source && r.EventId == eventId, cancellationToken))
        {
            return false;
        }

        set.Add(new ProcessedWebhook
        {
            Source = source,
            EventId = eventId,
            ReceivedAt = (clock ?? TimeProvider.System).GetUtcNow(),
        });
        return true;
    }
}
