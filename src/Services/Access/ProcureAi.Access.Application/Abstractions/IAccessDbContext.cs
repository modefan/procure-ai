using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IAccessDbContext
{
    DbSet<PermissionMatrix> PermissionMatrices { get; }

    DbSet<Department> Departments { get; }

    DbSet<CostCenter> CostCenters { get; }

    DbSet<TenantUser> TenantUsers { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
