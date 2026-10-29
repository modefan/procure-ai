namespace ProcureAi.Contracts.Budgets;

/// <summary>
/// Integration event: the amount of a cost center budget was changed (not a reservation). <c>FiscalYear</c> is the year the budget belongs to
/// (e.g. <c>FY2027</c>), <c>Amount</c> the new budget in Rand and <c>Version</c> the budget version after the change.
/// </summary>
public sealed record BudgetAmountSet(
    string TenantId,
    string CostCenterCode,
    string FiscalYear,
    decimal Amount,
    uint Version,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
