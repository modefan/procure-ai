using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReleaseBudgetHandler
{
    /// <summary>
    /// Frees whatever the request holds (compensation). <c>BudgetReleased</c> is published only when money was freed, so a
    /// repeated release is silent. When the request has no marker yet, the release came first: a released marker is stored
    /// so the reservation that follows is refused as cancelled. The marker row is the one a concurrent reserve of the same
    /// request also writes, so the two cannot both succeed; the loser is retried and sees the other's outcome.
    /// </summary>
    public static async Task Handle(
        ReleaseBudget command, IBudgetsDbContext db, IMessageBus bus, ICacheInvalidator cache, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var marker = await db.RequestMarkers.IgnoreQueryFilters().SingleOrDefaultAsync(
            m => m.TenantId == command.TenantId && m.RequestId == command.RequestId, cancellationToken);
        var budget = marker is { IsReleased: false }
            ? await db.Budgets.IgnoreQueryFilters().Include(b => b.Reservations).SingleOrDefaultAsync(
                b => b.TenantId == command.TenantId && b.Reservations.Any(r => r.RequestId == command.RequestId), cancellationToken)
            : null;

        var decision = BudgetDecisions.Release(command, marker, budget, time.GetUtcNow());
        switch (decision.Marker)
        {
            case MarkerChange.AddReleased:
                db.RequestMarkers.Add(RequestMarker.Released(command.TenantId, command.RequestId));
                break;
            case MarkerChange.MarkReleased:
                marker!.Release();
                break;
        }

        if (decision.Event is not null)
        {
            await cache.RemoveAfterCommitAsync([BudgetCacheTag.Tenant(command.TenantId)]);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (decision.Event is not null)
        {
            await bus.PublishAsync(decision.Event);
        }
    }
}
