namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event (saga): the cancel of an ordered request was refused. <c>RequesterId</c> (who raised the request, for the bell and
/// email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record RequestCancellationRefused(
    Guid RequestId,
    string TenantId,
    string Number,
    string Reason,
    DateTimeOffset OccurredAt,
    string? RequesterId = null) : IIntegrationEvent;
