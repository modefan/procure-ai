namespace ProcureAi.Contracts.Requests;

/// <summary>Integration event: a purchase request was submitted. Amounts are decimal Rand (ZAR).</summary>
public sealed record RequestSubmitted(
    Guid RequestId,
    string TenantId,
    string Number,
    string Title,
    string RequesterId,
    string CostCenterCode,
    decimal TotalInclVat,
    DateTimeOffset SubmittedAt) : IIntegrationEvent
{
    public DateTimeOffset OccurredAt => SubmittedAt;
}
