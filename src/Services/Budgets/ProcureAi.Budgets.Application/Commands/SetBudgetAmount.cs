using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

/// <summary>
/// Sets the budget of a cost center for a fiscal year (<c>FY2027</c>) in Rand. ExpectedVersion is the version the caller last
/// saw. Below what is reserved or spent is refused.
/// </summary>
public sealed record SetBudgetAmount(string CostCenterCode, string FiscalYear, decimal Amount, uint ExpectedVersion);

public static class SetBudgetAmountHandler
{
    public static async Task<BudgetDto> Handle(
        SetBudgetAmount command, IBudgetsDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var year = FiscalYearArgument.Parse(command.FiscalYear);
        var budget = await db.Budgets.Include(b => b.Reservations).SingleOrDefaultAsync(
            b => b.CostCenterCode == command.CostCenterCode && b.FiscalYear == year, cancellationToken)
            ?? throw new NotFoundException($"No budget for {command.CostCenterCode} in {year}.");
        OptimisticConcurrency.EnsureVersion($"Budget {command.CostCenterCode} {year}", budget.Version, command.ExpectedVersion);

        budget.SetAmount(Money.Zar(command.Amount));
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, "The budget", cancellationToken);
        var dto = BudgetMapper.ToDto(budget);
        // Through the outbox with the change, so a viewer of the budget list gets told (live update) and the edit lands in the business audit.
        await bus.PublishAsync(new BudgetAmountSet(budget.TenantId, budget.CostCenterCode, dto.FiscalYear, dto.Amount, dto.Version, time.GetUtcNow()));
        return dto;
    }
}
