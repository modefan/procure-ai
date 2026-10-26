using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReserveBudgetHandler
{
    /// <summary>
    /// Reserves the amount in the budget of the fiscal year the request was submitted in, then publishes the outcome through
    /// the outbox. A message delivered twice reserves once and repeats the same answer. A request released earlier is refused
    /// as cancelled. The budget row changes on every reservation, so a concurrent loser fails on the version and is retried.
    /// </summary>
    public static async Task Handle(
        ReserveBudget command, IBudgetsDbContext db, IMessageBus bus, IOptions<BudgetsOptions> options, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var year = FiscalYear.For(command.SubmittedAt, options.Value.Zone);
        var releasedEarlier = await db.ReleasedRequests.AnyAsync(
            r => r.TenantId == command.TenantId && r.RequestId == command.RequestId, cancellationToken);
        var budget = releasedEarlier
            ? null
            : await db.Budgets.Include(b => b.Reservations).SingleOrDefaultAsync(
                b => b.TenantId == command.TenantId && b.CostCenterCode == command.CostCenterCode && b.FiscalYear == year,
                cancellationToken);

        var result = BudgetDecisions.Reserve(command, year, budget, releasedEarlier, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(result);
    }
}
