using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Application.Queries;

/// <summary>Asks whether an amount in Rand fits what is left of the cost center budget of the current fiscal year.</summary>
public sealed record CheckBudget(string CostCenterCode, decimal Amount);

public static class CheckBudgetHandler
{
    public static async Task<BudgetCheckDto> Handle(
        CheckBudget query, IBudgetsDbContext db, IOptions<BudgetsOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        if (query.Amount < 0)
        {
            throw new DomainException("An amount cannot be negative.");
        }

        var year = FiscalYear.For(time.GetUtcNow(), options.Value.Zone);
        var budget = await db.Budgets.AsNoTracking().Include(b => b.Reservations)
            .SingleOrDefaultAsync(b => b.CostCenterCode == query.CostCenterCode && b.FiscalYear == year, cancellationToken);
        if (budget is null)
        {
            return new BudgetCheckDto(Fits: false, Available: 0m, year.ToString());
        }

        var available = budget.Available.Amount;
        return new BudgetCheckDto(query.Amount <= available, available, year.ToString());
    }
}
