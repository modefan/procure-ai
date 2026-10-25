namespace ProcureAi.Contracts.Budgets;

/// <summary>Command to Budgets: reserve the amount of a submitted request against its cost center.</summary>
public sealed record ReserveBudget(
    Guid RequestId,
    string TenantId,
    string CostCenterCode,
    decimal Amount,
    DateTimeOffset SubmittedAt);
