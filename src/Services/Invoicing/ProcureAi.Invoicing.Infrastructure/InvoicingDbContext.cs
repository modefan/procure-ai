using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Infrastructure;

/// <summary>
/// Invoicing's data: the order views (copies of Purchasing's orders), the invoices with their lines and payments, the audit rows,
/// the arrival and payment numbers and the processed webhooks. Every write of an invoice or an order view changes its own row
/// (<c>Revision</c>), so the row's <c>xmin</c> guards the whole aggregate; nothing needs touching here.
/// </summary>
public sealed class InvoicingDbContext(DbContextOptions<InvoicingDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IInvoicingDbContext, IAuditContextSource
{
    /// <summary>
    /// The tenant every query is limited to. Without a caller (migrations, webhooks) nothing matches; an invoice that waits without
    /// a tenant (<see cref="Invoice.UnassignedTenant"/>) never matches either. The handlers that run without a caller (messages
    /// carrying the tenant, webhooks whose tenant is the order view's) ignore the filter on purpose.
    /// </summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context's scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<OrderView> Orders => Set<OrderView>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InvoicingDbContext).Assembly);
        modelBuilder.Entity<OrderView>().HasQueryFilter(o => o.TenantId == CurrentTenantId && o.TenantId != "");
        modelBuilder.Entity<Invoice>().HasQueryFilter(i => i.TenantId == CurrentTenantId && i.TenantId != Invoice.UnassignedTenant);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
        // Arrival numbers of invoices and payment numbers (PAY-0001): one counter per tenant and name.
        modelBuilder.AddTenantSequences();
        // Replay protection of the supplier's invoice webhooks, committed with their effect.
        modelBuilder.AddProcessedWebhooks();
    }
}
