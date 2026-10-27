namespace ProcureAi.Contracts.Budgets;

/// <summary>Integration event: spend was recorded on a cost center for a payment (decimal Rand, ZAR).</summary>
public sealed record BudgetSpent(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    Guid PaymentId,
    decimal Amount,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
