namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event: the request was rejected by an approver, or by the system (<c>RejectedBy</c> is null, e.g. the budget was refused).
/// <c>RequesterId</c> (who raised the request, for the bell and email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record RequestRejected(
    Guid RequestId,
    string TenantId,
    string Number,
    string Reason,
    string? RejectedBy,
    DateTimeOffset OccurredAt,
    string? RequesterId = null) : IIntegrationEvent;
