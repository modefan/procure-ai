using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Application.Dtos;

/// <summary>A purchase order as the API returns it. Amounts are decimal Rand, plus display strings like <c>R 86 940,00</c>.</summary>
public sealed record PurchaseOrderDto(
    Guid Id,
    Guid RequestId,
    string Number,
    string RequestNumber,
    string RequesterId,
    string CostCenterCode,
    string SupplierId,
    OrderStatus Status,
    string SupplierStatus,
    string? SupplierReference,
    DateOnly? ExpectedDelivery,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<GoodsReceiptDto> Receipts,
    decimal TotalExclVat,
    decimal Vat,
    decimal TotalInclVat,
    string TotalExclVatDisplay,
    string VatDisplay,
    string TotalInclVatDisplay,
    DateTimeOffset CreatedAt,
    string? CancelReason,
    DateTimeOffset? CancelledAt,
    uint Version);

public sealed record OrderLineDto(
    int LineNo,
    string Sku,
    string Description,
    int Ordered,
    int Received,
    int ClosedShort,
    int Outstanding,
    decimal UnitPriceExclVat,
    decimal TotalExclVat,
    string UnitPriceExclVatDisplay,
    string TotalExclVatDisplay);

public sealed record GoodsReceiptDto(
    Guid Id,
    string ReceivedBy,
    DateTimeOffset ReceivedAt,
    string? DeliveryNote,
    IReadOnlyList<ReceiptLineDto> Lines);

public sealed record ReceiptLineDto(int LineNo, int Quantity);
