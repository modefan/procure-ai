using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Domain;

namespace ProcureAi.Budgets.Application.Mapping;

/// <summary>Domain to DTO. Written by hand: every field is derived from the Money and FiscalYear value objects.</summary>
public static class BudgetMapper
{
    public static BudgetDto ToDto(CostCenterBudget budget) => new(
        budget.CostCenterCode, budget.FiscalYear.ToString(), budget.Amount.Amount, budget.Reserved.Amount,
        budget.Spent.Amount, budget.Available.Amount, budget.Version);
}
