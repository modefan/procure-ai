using System.ComponentModel.DataAnnotations;
using ProcureAi.Requests.Application.Commands;

namespace ProcureAi.Requests.Api.Controllers;

/// <summary>A line of a request. The unit price is Rand excl. VAT.</summary>
public sealed record LineBody(
    [Required, StringLength(RequestLimits.CodeLength)] string Sku,
    [Required, StringLength(RequestLimits.TextLength)] string Description,
    [Range(1, RequestLimits.MaxQuantity)] int Quantity,
    [Range(0.01, RequestLimits.MaxUnitPrice)] decimal UnitPriceExclVat)
{
    internal LineInput ToInput() => new(Sku, Description, Quantity, UnitPriceExclVat);
}

public sealed record CreateRequestBody(
    [Required, StringLength(RequestLimits.TextLength)] string Title,
    [Required, StringLength(RequestLimits.CodeLength)] string CostCenterCode,
    [Required, StringLength(RequestLimits.CodeLength)] string SupplierId,
    [Required, MaxLength(RequestLimits.MaxLines)] IReadOnlyList<LineBody> Lines)
{
    internal CreateRequest ToCommand() => new(Title, CostCenterCode, SupplierId, [.. Lines.Select(l => l.ToInput())]);
}

public sealed record UpdateRequestBody(
    [Required, StringLength(RequestLimits.TextLength)] string Title,
    [Required, MaxLength(RequestLimits.MaxLines)] IReadOnlyList<LineBody> Lines)
{
    internal UpdateRequest ToCommand(Guid id, uint expectedVersion) =>
        new(id, expectedVersion, Title, [.. Lines.Select(l => l.ToInput())]);
}

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class RequestLimits
{
    public const int CodeLength = 50;
    public const int TextLength = 200;
    public const int MaxLines = 100;
    public const int MaxQuantity = 100_000;
    public const double MaxUnitPrice = 100_000_000;
}
