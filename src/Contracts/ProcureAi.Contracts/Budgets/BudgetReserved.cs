namespace ProcureAi.Contracts.Budgets;

/// <summary>Integration event: the amount was reserved. <c>FiscalYear</c> is the year the budget belongs to, e.g. <c>FY2027</c> (1 March 2026 to the end of February 2027).</summary>
public sealed record BudgetReserved(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    string FiscalYear,
    decimal Amount,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
