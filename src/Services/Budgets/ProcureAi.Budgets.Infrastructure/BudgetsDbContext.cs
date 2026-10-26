using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;

namespace ProcureAi.Budgets.Infrastructure;

public sealed class BudgetsDbContext(DbContextOptions<BudgetsDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IBudgetsDbContext, IAuditContextSource
{
    /// <summary>The tenant every query is limited to. Without a caller (migrations) nothing matches.</summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<CostCenterBudget> Budgets => Set<CostCenterBudget>();

    public DbSet<RequestMarker> RequestMarkers => Set<RequestMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BudgetsDbContext).Assembly);
        modelBuilder.Entity<CostCenterBudget>().HasQueryFilter(b => b.TenantId == CurrentTenantId);
        modelBuilder.Entity<RequestMarker>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
    }
}
