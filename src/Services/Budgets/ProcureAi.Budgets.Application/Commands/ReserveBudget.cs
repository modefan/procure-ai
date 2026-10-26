using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReserveBudgetHandler
{
    /// <summary>
    /// Reserves the amount in the budget of the fiscal year the request was submitted in, then publishes the outcome through
    /// the outbox. A message delivered twice reserves once and repeats the same answer.
    /// </summary>
    public static async Task Handle(
        ReserveBudget command, IBudgetsDbContext db, IMessageBus bus, IOptions<BudgetsOptions> options, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var year = FiscalYear.For(command.SubmittedAt, options.Value.Zone);
        var budget = await db.Budgets.Include(b => b.Reservations).SingleOrDefaultAsync(
            b => b.TenantId == command.TenantId && b.CostCenterCode == command.CostCenterCode && b.FiscalYear == year,
            cancellationToken);

        if (budget is null)
        {
            await bus.PublishAsync(new BudgetReservationFailed(
                command.RequestId, command.TenantId, command.CostCenterCode, $"No budget for {command.CostCenterCode} in {year}",
                command.Amount, 0m, time.GetUtcNow()));
            return;
        }

        var outcome = budget.Reserve(command.RequestId, Money.Zar(command.Amount));
        await db.SaveChangesAsync(cancellationToken);

        if (outcome.IsReserved)
        {
            await bus.PublishAsync(new BudgetReserved(
                command.RequestId, command.TenantId, command.CostCenterCode, year.ToString(), outcome.Amount.Amount, time.GetUtcNow()));
        }
        else
        {
            await bus.PublishAsync(new BudgetReservationFailed(
                command.RequestId, command.TenantId, command.CostCenterCode, outcome.Reason!, command.Amount,
                outcome.Available.Amount, time.GetUtcNow()));
        }
    }
}
