namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event (saga): the request is fully paid (decimal Rand, ZAR). <c>RequesterId</c> (who raised the request, for the bell and
/// email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record RequestCompleted(
    Guid RequestId,
    string TenantId,
    string Number,
    decimal SpentInclVat,
    DateTimeOffset OccurredAt,
    string? RequesterId = null) : IIntegrationEvent;
