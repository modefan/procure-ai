namespace ProcureAi.Budgets.Application.Dtos;

/// <summary>
/// A cost center budget in Rand. Version is the ETag the next change must send in If-Match. OwnerName is the display name of
/// the cost center's manager, filled in by the API from Access (null when the cost center has no manager or Access cannot say).
/// </summary>
public sealed record BudgetDto(
    string CostCenterCode, string FiscalYear, decimal Amount, decimal Reserved, decimal Spent, decimal Available, uint Version, string? OwnerName = null);

/// <summary>Whether an amount fits the budget of the current fiscal year, and what is left.</summary>
public sealed record BudgetCheckDto(bool Fits, decimal Available, string FiscalYear);
