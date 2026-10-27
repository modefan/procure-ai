using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Infrastructure;

public sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IPurchasingDbContext, IAuditContextSource
{
    /// <summary>
    /// The tenant every query is limited to. Without a caller (migrations, webhooks) nothing matches; the handlers that run without
    /// one (messages carrying the tenant, webhooks whose tenant is the stored order's) ignore the filter on purpose.
    /// </summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context's scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PurchasingDbContext).Assembly);
        modelBuilder.Entity<PurchaseOrder>().HasQueryFilter(o => o.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
        // Order numbers (PO-1001): one counter per tenant, bumped by TenantSequence.
        modelBuilder.AddTenantSequences();
        // Replay protection of the supplier webhooks, committed with their effect.
        modelBuilder.AddProcessedWebhooks();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TouchOrdersWithChangedParts();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        TouchOrdersWithChangedParts();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The version of the order row (<c>xmin</c>) guards the whole aggregate, but EF Core only writes the order row, and so only
    /// checks its version, when one of its own columns changed. A second partial receipt changes only lines and receipts: without
    /// this, two concurrent receipts (or a receipt and anything else that leaves the order row alone) would both commit and
    /// over-receive. Every order with a changed line, receipt or receipt line is therefore marked modified, so its UPDATE carries
    /// the version check and moves the version.
    /// </summary>
    private void TouchOrdersWithChangedParts()
    {
        var entries = ChangeTracker.Entries().ToList();
        var orderOfReceipt = entries
            .Where(e => e.Entity is GoodsReceipt)
            .ToDictionary(e => ((GoodsReceipt)e.Entity).Id, e => (Guid)e.Property(PurchaseOrderConfiguration.OrderForeignKey).CurrentValue!);
        var touched = new HashSet<Guid>();
        foreach (var entry in entries.Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            switch (entry.Entity)
            {
                case OrderLine or GoodsReceipt:
                    touched.Add((Guid)entry.Property(PurchaseOrderConfiguration.OrderForeignKey).CurrentValue!);
                    break;
                case ReceiptLine when orderOfReceipt.TryGetValue(
                    (Guid)entry.Property(PurchaseOrderConfiguration.ReceiptForeignKey).CurrentValue!, out var orderId):
                    touched.Add(orderId);
                    break;
            }
        }

        foreach (var order in entries.Where(e => e.Entity is PurchaseOrder o && touched.Contains(o.Id) && e.State == EntityState.Unchanged))
        {
            order.State = EntityState.Modified;
        }
    }
}
