namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event (saga): the request is fully paid (decimal Rand, ZAR).</summary>
public sealed record RequestCompleted(
    Guid RequestId,
    string TenantId,
    string Number,
    decimal SpentInclVat,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
