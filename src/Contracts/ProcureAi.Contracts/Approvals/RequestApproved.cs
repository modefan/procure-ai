namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event: every step is approved. Amounts are decimal Rand (ZAR). <c>RequesterId</c> (who raised the request, for the bell
/// and email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record RequestApproved(
    Guid RequestId,
    string TenantId,
    string Number,
    decimal TotalInclVat,
    IReadOnlyList<string> ApproverIds,
    DateTimeOffset OccurredAt,
    string? RequesterId = null) : IIntegrationEvent;
