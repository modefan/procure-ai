using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProcureAi.Budgets.Domain;

namespace ProcureAi.Budgets.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IBudgetsDbContext
{
    /// <summary>The budgets, with their reservations loaded when asked for with Include.</summary>
    DbSet<CostCenterBudget> Budgets { get; }

    /// <summary>Tenant-level tombstones of requests released before their reservation arrived.</summary>
    DbSet<ReleasedRequest> ReleasedRequests { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
