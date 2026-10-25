namespace ProcureAi.Contracts.Requests;

/// <summary>Integration event: the requester (or an administrator) cancelled a request; its budget is released.</summary>
public sealed record RequestCancelled(
    Guid RequestId,
    string TenantId,
    string Number,
    string CancelledBy,
    string? Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
