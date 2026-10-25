namespace ProcureAi.Contracts.Budgets;

/// <summary>Integration event: the amount could not be reserved (<c>Available</c> was less than <c>Requested</c>, or the budget does not exist).</summary>
public sealed record BudgetReservationFailed(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    string Reason,
    decimal Requested,
    decimal Available,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
