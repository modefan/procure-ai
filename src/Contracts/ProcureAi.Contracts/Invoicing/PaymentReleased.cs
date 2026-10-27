namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: a payment was released (decimal Rand, ZAR).</summary>
public sealed record PaymentReleased(
    Guid PaymentId,
    string PaymentNumber,
    Guid InvoiceId,
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    decimal AmountInclVat,
    IReadOnlyList<PaidLine> Lines,
    string ReleasedBy,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
