namespace ProcureAi.Contracts.Budgets;

/// <summary>Command to Budgets: release whatever is reserved for the request (compensation); safe to send twice.</summary>
public sealed record ReleaseBudget(
    Guid RequestId,
    string TenantId);
