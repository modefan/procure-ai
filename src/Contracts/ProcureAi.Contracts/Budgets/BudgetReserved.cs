namespace ProcureAi.Contracts.Budgets;

/// <summary>Integration event: the amount was reserved. <c>FiscalYear</c> is the year the budget belongs to, e.g. <c>2026</c>.</summary>
public sealed record BudgetReserved(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    string FiscalYear,
    decimal Amount,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
