namespace ProcureAi.Contracts.Budgets;

/// <summary>Integration event: a reservation was released.</summary>
public sealed record BudgetReleased(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    decimal Amount,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
