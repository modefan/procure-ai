namespace ProcureAi.Requests.Domain;

/// <summary>Domain event: a purchase request was submitted. Application maps it to the integration event.</summary>
public sealed record PurchaseRequestSubmitted(
    Guid RequestId,
    string TenantId,
    string Number,
    string Title,
    string RequesterId,
    string CostCenterCode,
    decimal TotalInclVat,
    DateTimeOffset SubmittedAt,
    string SupplierId,
    IReadOnlyList<RequestLine> Lines);
