namespace ProcureAi.Assistant.Domain;

/// <summary>
/// A stored scan (MongoDB, deleted after <c>Assistant:Scanning:RetentionDays</c>): what the model read from the invoice and the warnings of
/// the checks. It is a reading aid for the person who uploaded the PDF and the source of the document stored with an invoice that
/// was recorded from it; the Assistant never writes an invoice, and the lines of an invoice are the ones the user reviewed.
/// </summary>
public sealed record InvoiceScan(
    Guid Id, string TenantId, string UserId, DateTimeOffset CreatedAt, string Model, string Confidence, ScannedInvoice Invoice,
    IReadOnlyList<ScanWarning> Warnings);
