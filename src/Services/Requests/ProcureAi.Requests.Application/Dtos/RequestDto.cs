using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Dtos;

/// <summary>A purchase request as the API returns it. Amounts are decimal Rand, plus display strings like <c>R 86 940,00</c>.</summary>
public sealed record RequestDto(
    Guid Id,
    string Number,
    string Title,
    RequestStatus Status,
    string CostCenterCode,
    string SupplierId,
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
