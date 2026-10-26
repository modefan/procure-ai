using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReleaseBudgetHandler
{
    /// <summary>
    /// Frees whatever the request holds (compensation). <c>BudgetReleased</c> is published only when money was freed, so a
    /// repeated release is silent. When no budget holds the request yet, the release came first: a tenant-level tombstone is
    /// stored so the reservation that follows is refused as cancelled.
    /// </summary>
    public static async Task Handle(
        ReleaseBudget command, IBudgetsDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var budget = await db.Budgets.Include(b => b.Reservations).SingleOrDefaultAsync(
            b => b.TenantId == command.TenantId && b.Reservations.Any(r => r.RequestId == command.RequestId), cancellationToken);

        var decision = BudgetDecisions.Release(command, budget, time.GetUtcNow());
        if (decision.RecordTombstone
            && !await db.ReleasedRequests.AnyAsync(
                r => r.TenantId == command.TenantId && r.RequestId == command.RequestId, cancellationToken))
        {
            db.ReleasedRequests.Add(ReleasedRequest.Create(command.TenantId, command.RequestId));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (decision.Event is not null)
        {
            await bus.PublishAsync(decision.Event);
        }
    }
}
