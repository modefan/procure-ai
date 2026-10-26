using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Application.Mapping;

namespace ProcureAi.Budgets.Application.Queries;

/// <summary>The budgets of the tenant; FiscalYear (<c>FY2027</c>) narrows them to one year, null lists every year.</summary>
public sealed record ListBudgets(string? FiscalYear);

public static class ListBudgetsHandler
{
    public static async Task<IReadOnlyList<BudgetDto>> Handle(ListBudgets query, IBudgetsDbContext db, CancellationToken cancellationToken)
    {
        var budgets = db.Budgets.AsNoTracking().Include(b => b.Reservations).AsQueryable();
        if (query.FiscalYear is not null)
        {
            var year = FiscalYearArgument.Parse(query.FiscalYear);
            budgets = budgets.Where(b => b.FiscalYear == year);
        }

        var found = await budgets.OrderBy(b => b.CostCenterCode).ThenBy(b => b.FiscalYear).ToListAsync(cancellationToken);
        return [.. found.Select(BudgetMapper.ToDto)];
    }
}
