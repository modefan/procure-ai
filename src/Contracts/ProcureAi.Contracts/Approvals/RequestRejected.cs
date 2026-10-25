namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: the request was rejected by an approver, or by the system (<c>RejectedBy</c> is null, e.g. the budget was refused).</summary>
public sealed record RequestRejected(
    Guid RequestId,
    string TenantId,
    string Number,
    string Reason,
    string? RejectedBy,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
