using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Infrastructure;

public sealed class RequestsDbContext(DbContextOptions<RequestsDbContext> options, ICallerContext? caller = null)
    : DbContext(options), IRequestsDbContext, IAuditContextSource
{
    /// <summary>The tenant every query is limited to. Without a caller (migrations) nothing matches.</summary>
    public string CurrentTenantId => caller?.TenantId ?? "";

    /// <summary>The caller of this context's scope, for the audit interceptor.</summary>
    public IAuditContext? AuditContext => caller is null ? null : new CallerAuditContext(caller);

    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();

    public DbSet<RequestNumberCounter> RequestNumberCounters => Set<RequestNumberCounter>();

    public void MarkModified(PurchaseRequest request) => Entry(request).State = EntityState.Modified;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestsDbContext).Assembly);
        modelBuilder.Entity<PurchaseRequest>().HasQueryFilter(r => r.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
    }
}

/// <summary>Last request number handed out per tenant (one row per tenant).</summary>
public sealed class RequestNumberCounter
{
    public string TenantId { get; set; } = "";

    public int LastValue { get; set; }
}
