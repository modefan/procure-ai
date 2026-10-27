namespace ProcureAi.Contracts.Requests;

/// <summary>
/// Integration event: a purchase request was submitted. Amounts are decimal Rand (ZAR). <c>SupplierId</c> and <c>Lines</c> are what
/// Purchasing later orders. <b>A RequestId is never reused</b>: the release marker in Budgets and the cancellation tombstone in
/// Purchasing for a request are permanent.
/// </summary>
public sealed record RequestSubmitted(
    Guid RequestId,
    string TenantId,
    string Number,
    string Title,
    string RequesterId,
    string CostCenterCode,
    decimal TotalInclVat,
    DateTimeOffset SubmittedAt,
    string SupplierId,
    IReadOnlyList<RequestLineItem> Lines) : IIntegrationEvent
{
    public DateTimeOffset OccurredAt => SubmittedAt;
}
