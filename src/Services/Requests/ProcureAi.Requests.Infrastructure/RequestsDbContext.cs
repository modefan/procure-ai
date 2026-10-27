using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Infrastructure;

public sealed class RequestsDbContext(DbContextOptions<RequestsDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IRequestsDbContext, IAuditContextSource
{
    /// <summary>The tenant every query is limited to. Without a caller (migrations) nothing matches.</summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context's scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();

    public void MarkModified(PurchaseRequest request) => Entry(request).State = EntityState.Modified;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestsDbContext).Assembly);
        modelBuilder.Entity<PurchaseRequest>().HasQueryFilter(r => r.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
        // Request numbers (PR-1043): one counter per tenant, bumped by TenantSequence.
        modelBuilder.AddTenantSequences();
    }
}
