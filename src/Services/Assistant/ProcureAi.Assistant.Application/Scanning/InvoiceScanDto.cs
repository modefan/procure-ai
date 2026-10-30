using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Scanning;

/// <summary>
/// A scanned invoice as the API returns it: what the model read (its numbers as read, never corrected), the warnings of the checks (each
/// names the recalculated value next to the model's), the confidence (<c>high</c>, <c>medium</c>, <c>low</c>) and the model that read it.
/// The user reviews it and enters the invoice; <c>invoiceDate</c> is null when the model gave none or one that is not a date (a warning
/// says so).
/// </summary>
public sealed record InvoiceScanDto(
    Guid Id,
    string SupplierName,
    string? SupplierVatNumber,
    string InvoiceNumber,
    DateOnly? InvoiceDate,
    string? OrderNumber,
    IReadOnlyList<ScannedLineDto> Lines,
    decimal Vat,
    decimal TotalExclVat,
    decimal TotalInclVat,
    string Confidence,
    IReadOnlyList<ScanWarningDto> Warnings,
    string Model)
{
    public static InvoiceScanDto From(InvoiceScan scan)
    {
        var invoice = scan.Invoice;
        return new InvoiceScanDto(
            scan.Id, invoice.SupplierName, invoice.SupplierVatNumber, invoice.InvoiceNumber, InvoiceValidator.ParseDate(invoice.InvoiceDate),
            invoice.OrderNumber,
            [.. invoice.Lines.Select(l => new ScannedLineDto(l.Description, l.Quantity, l.UnitPriceExclVat, l.LineTotalExclVat))],
            invoice.Vat, invoice.TotalExclVat, invoice.TotalInclVat, scan.Confidence,
            [.. scan.Warnings.Select(w => new ScanWarningDto(w.Code, w.Message))], scan.Model);
    }
}

/// <summary>One line as the model read it. The quantity is a decimal: a fractional one is what was read (and is warned about).</summary>
public sealed record ScannedLineDto(string Description, decimal Quantity, decimal UnitPriceExclVat, decimal LineTotalExclVat);

/// <summary>A failed check: <c>code</c> (<c>line_total</c>, <c>lines_sum</c>, <c>vat</c>, <c>total</c>, <c>quantity</c>, <c>future_date</c>, <c>date</c>, <c>no_lines</c>,
/// <c>negative_amount</c>, <c>amount_out_of_range</c>) and a sentence for people.</summary>
public sealed record ScanWarningDto(string Code, string Message);

/// <summary>
/// A stored scan for another service (the internal read): its tenant, who scanned it and when, and the scan. Invoicing keeps <c>scan</c>
/// as the document of an invoice recorded from it.
/// </summary>
public sealed record InvoiceScanRecordDto(Guid Id, string TenantId, string UserId, DateTimeOffset CreatedAt, InvoiceScanDto Scan)
{
    public static InvoiceScanRecordDto From(InvoiceScan scan) => new(scan.Id, scan.TenantId, scan.UserId, scan.CreatedAt, InvoiceScanDto.From(scan));
}
