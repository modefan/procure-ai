namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: an invoice arrived (decimal Rand, ZAR). <c>Source</c> says how (manual entry, scan, supplier).</summary>
public sealed record InvoiceReceived(
    Guid InvoiceId,
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    string SupplierId,
    string SupplierInvoiceNumber,
    string Source,
    decimal TotalInclVat,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
