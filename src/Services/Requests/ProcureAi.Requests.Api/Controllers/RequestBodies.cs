using System.ComponentModel.DataAnnotations;
using ProcureAi.Requests.Application.Commands;

namespace ProcureAi.Requests.Api.Controllers;

/// <summary>A line of a request: a SKU of the supplier's catalog and a quantity. Description and unit price come from the catalog.</summary>
public sealed record LineBody(
    [Required, StringLength(RequestLimits.CodeLength)] string Sku,
    [Range(1, RequestLimits.MaxQuantity)] int Quantity)
{
    internal LineInput ToInput() => new(Sku, Quantity);
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

/// <summary>Why the requester cancels (optional).</summary>
public sealed record CancelRequestBody([StringLength(RequestLimits.ReasonLength)] string? Reason)
{
    internal CancelRequest ToCommand(Guid id, uint expectedVersion) => new(id, expectedVersion, Reason);
}

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class RequestLimits
{
    public const int CodeLength = 50;
    public const int TextLength = 200;
    public const int ReasonLength = 500;
    public const int MaxLines = 100;
    public const int MaxQuantity = 100_000;
}
