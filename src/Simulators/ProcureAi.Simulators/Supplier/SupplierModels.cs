using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Simulators.Supplier;

// The supplier's own vocabulary (what a real vendor's order API would look like). Purchasing's anti-corruption layer translates.

public sealed record OrderLineDto(
    [Range(1, int.MaxValue)] int LineNumber,
    [Required, StringLength(64, MinimumLength = 1)] string ArticleNumber,
    [Range(1, int.MaxValue)] int Quantity,
    [Range(0, long.MaxValue)] long NetPriceCents);

public sealed record CreateOrderRequest(
    [Required, StringLength(128, MinimumLength = 1)] string ExternalReference,
    [Required, StringLength(128, MinimumLength = 1)] string BuyerReference,
    [Required] string Currency,
    [Required, MinLength(1)] List<OrderLineDto> Lines,
    [StringLength(100)] string? SupplierCode = null);

public sealed record OrderReceipt(string SupplierOrderId, string Status);

public sealed record CancelReceipt(string Status);

public sealed record InvoiceLineOverride(int LineNumber, [Range(1, int.MaxValue)] int Quantity);

/// <summary>Admin hook: send an invoice now. Lines default to every ordered line at the ordered quantity; the deviation is added to each price.</summary>
public sealed record IssueInvoiceRequest(List<InvoiceLineOverride>? Lines, int? PriceDeviationCents, string? InvoiceNumber);

public sealed record InvoiceReceipt(string InvoiceNumber);

// Webhook bodies, serialized as camelCase JSON, nulls omitted.

public sealed record OrderWebhook(
    Guid EventId,
    string Type,
    string SupplierOrderId,
    string ExternalReference,
    DateOnly? ExpectedDeliveryDate,
    string? DeclineReason);

public sealed record InvoiceWebhook(
    Guid EventId,
    string Type,
    string InvoiceNumber,
    string SupplierCode,
    string ExternalReference,
    DateOnly IssueDate,
    IReadOnlyList<OrderLineDto> Lines,
    long NetTotalCents,
    long VatCents,
    long GrossTotalCents);
