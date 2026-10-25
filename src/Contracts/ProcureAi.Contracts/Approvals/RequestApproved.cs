namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: every step is approved. Amounts are decimal Rand (ZAR).</summary>
public sealed record RequestApproved(
    Guid RequestId,
    string TenantId,
    string Number,
    decimal TotalInclVat,
    IReadOnlyList<string> ApproverIds,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
