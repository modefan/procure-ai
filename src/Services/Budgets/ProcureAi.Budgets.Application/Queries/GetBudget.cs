using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Budgets.Application.Queries;

public sealed record GetBudget(string CostCenterCode, string FiscalYear);

public static class GetBudgetHandler
{
    public static async Task<BudgetDto> Handle(GetBudget query, IBudgetsDbContext db, CancellationToken cancellationToken)
    {
        var year = FiscalYearArgument.Parse(query.FiscalYear);
        var budget = await db.Budgets.AsNoTracking().Include(b => b.Reservations)
            .SingleOrDefaultAsync(b => b.CostCenterCode == query.CostCenterCode && b.FiscalYear == year, cancellationToken)
            ?? throw new NotFoundException($"No budget for {query.CostCenterCode} in {year}.");
        return BudgetMapper.ToDto(budget);
    }
}
