using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;

namespace ProcureAi.Access.Infrastructure;

public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IAccessDbContext, IAuditContextSource
{
    /// <summary>The tenant every query is limited to. Without a caller (migrations) nothing matches.</summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<PermissionMatrix> PermissionMatrices => Set<PermissionMatrix>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<CostCenter> CostCenters => Set<CostCenter>();

    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccessDbContext).Assembly);
        modelBuilder.Entity<PermissionMatrix>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        modelBuilder.Entity<Department>().HasQueryFilter(d => d.TenantId == CurrentTenantId);
        modelBuilder.Entity<CostCenter>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantUser>().HasQueryFilter(u => u.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
    }
}
