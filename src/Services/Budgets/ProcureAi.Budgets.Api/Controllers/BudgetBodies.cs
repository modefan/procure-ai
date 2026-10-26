using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Budgets.Api.Controllers;

/// <summary>The new budget of the cost center for the fiscal year, in Rand including VAT.</summary>
public sealed record SetBudgetBody([Range(0, BudgetLimits.MaxAmount)] decimal Amount);

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class BudgetLimits
{
    public const int MaxCodeLength = 50;
    public const int MaxFiscalYearLength = 10;
    public const double MaxAmount = 1_000_000_000_000;
}
