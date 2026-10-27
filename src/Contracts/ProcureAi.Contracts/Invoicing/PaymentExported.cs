namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: the payment reached the ERP, which answered with <c>ErpReference</c>.</summary>
public sealed record PaymentExported(
    Guid PaymentId,
    Guid InvoiceId,
    Guid RequestId,
    string TenantId,
    string ErpReference,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
