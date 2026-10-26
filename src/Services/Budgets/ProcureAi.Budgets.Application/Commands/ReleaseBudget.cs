using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReleaseBudgetHandler
{
    /// <summary>
    /// Frees whatever the request holds (compensation). <c>BudgetReleased</c> is published only when money was freed, so a
    /// release that is repeated, or that arrives for a request that never reserved, is silent.
    /// </summary>
    public static async Task Handle(
        ReleaseBudget command, IBudgetsDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var budget = await db.Budgets.Include(b => b.Reservations).SingleOrDefaultAsync(
            b => b.TenantId == command.TenantId && b.Reservations.Any(r => r.RequestId == command.RequestId), cancellationToken);
        if (budget is null)
        {
            return;
        }

        var released = budget.Release(command.RequestId);
        await db.SaveChangesAsync(cancellationToken);

        if (released.Amount > 0m)
        {
            await bus.PublishAsync(new BudgetReleased(
                command.RequestId, command.TenantId, budget.CostCenterCode, released.Amount, time.GetUtcNow()));
        }
    }
}
