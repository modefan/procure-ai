using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Dtos;

/// <summary>
/// A purchase request as the API returns it. Amounts are decimal Rand, plus display strings like <c>R 86 940,00</c>.
/// <paramref name="RequesterId"/> is the user who raised it (the only one who may edit or cancel it); the name is not stored here.
/// </summary>
public sealed record RequestDto(
    Guid Id,
    string Number,
    string Title,
    RequestStatus Status,
    string CostCenterCode,
    string SupplierId,
    string RequesterId,
    IReadOnlyList<RequestLineDto> Lines,
    decimal TotalExclVat,
    decimal Vat,
    decimal TotalInclVat,
    string TotalExclVatDisplay,
    string VatDisplay,
    string TotalInclVatDisplay,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RejectedAt,
    string? RejectionReason,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    Guid? OrderId,
    string? OrderNumber,
    decimal? SpentInclVat,
    string? SpentInclVatDisplay,
    string? CancellationRefusedReason,
    uint Version);

public sealed record RequestLineDto(
    int LineNo,
    string Sku,
    string Description,
    int Quantity,
    decimal UnitPriceExclVat,
    decimal TotalExclVat,
    string UnitPriceExclVatDisplay,
    string TotalExclVatDisplay);
