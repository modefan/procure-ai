using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProcureAi.Budgets.Domain;

namespace ProcureAi.Budgets.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IBudgetsDbContext
{
    /// <summary>The budgets, with their reservations loaded when asked for with Include.</summary>
    DbSet<CostCenterBudget> Budgets { get; }

    /// <summary>One row per reserved or released request; the unique key that serialises reserve against release.</summary>
    DbSet<RequestMarker> RequestMarkers { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
