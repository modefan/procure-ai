using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Application.Dtos;

/// <summary>An invoice as the API returns it. Amounts are decimal Rand, plus display strings like <c>R 86 940,00</c>.</summary>
public sealed record InvoiceDto(
    Guid Id,
    Guid OrderId,
    string? OrderNumber,
    Guid RequestId,
    string SupplierId,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    long Sequence,
    InvoiceSource Source,
    InvoiceStatus Status,
    bool OnHold,
    string? HoldReason,
    IReadOnlyList<string> MismatchReasons,
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<PaymentDto> Payments,
    decimal TotalExclVat,
    decimal Vat,
    decimal TotalInclVat,
    string TotalExclVatText,
    string VatText,
    string TotalInclVatText,
    DateTimeOffset ReceivedAt,
    uint Version);

/// <summary>
/// An invoice line with the outcome of its match (screen 11): what was ordered at which price, what was received, what earlier
/// invoices claimed, and what this one invoices. The match fields are null until it was matched; the order fields are null while the
/// order is not known or the line is not on it.
/// </summary>
public sealed record InvoiceLineDto(
    int LineNo,
    string Sku,
    string Description,
    int Quantity,
    int Paid,
    decimal UnitPriceExclVat,
    decimal LineTotalExclVat,
    string UnitPriceText,
    string LineTotalText,
    int? Ordered,
    decimal? OrderUnitPriceExclVat,
    string? OrderUnitPriceText,
    bool? PriceOk,
    bool? QuantityOk,
    int? Received,
    int? InvoicedBefore,
    string? MatchReason);

public sealed record PaymentDto(
    Guid Id,
    string Number,
    IReadOnlyList<PaymentLineDto> Lines,
    decimal TotalExclVat,
    decimal Vat,
    decimal TotalInclVat,
    string TotalInclVatText,
    string ReleasedBy,
    DateTimeOffset ReleasedAt,
    ExportStatus ExportStatus,
    string? ErpReference,
    int ExportAttempts,
    string? LastExportError);

public sealed record PaymentLineDto(int LineNo, string Sku, int Quantity, decimal AmountExclVat, string AmountText);
