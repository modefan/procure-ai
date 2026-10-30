namespace ProcureAi.Assistant.Domain;

/// <summary>
/// An invoice as the model read it (the structured output of the <c>scan</c> task): its words and numbers, not checked. The date stays the
/// text the model returned (<c>yyyy-MM-dd</c> asked for), so an unreadable date is a warning instead of a failed scan.
/// </summary>
public sealed record ScannedInvoice(
    string SupplierName,
    string? SupplierVatNumber,
    string InvoiceNumber,
    string? InvoiceDate,
    string? OrderNumber,
    IReadOnlyList<ScannedLine> Lines,
    decimal Vat,
    decimal TotalExclVat,
    decimal TotalInclVat);
