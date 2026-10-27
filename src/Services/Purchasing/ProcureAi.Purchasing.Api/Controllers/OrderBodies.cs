using System.ComponentModel.DataAnnotations;
using ProcureAi.Purchasing.Application.Commands;

namespace ProcureAi.Purchasing.Api.Controllers;

/// <summary>How many of one order line arrived.</summary>
public sealed record ReceiptLineBody(
    [Range(1, OrderLimits.MaxLines)] int LineNo,
    [Range(1, OrderLimits.MaxQuantity)] int Quantity);

/// <summary>A delivery: the quantities per line that arrived, and the supplier's delivery note number if there is one.</summary>
public sealed record ReceiveGoodsBody(
    [Required, MinLength(1), MaxLength(OrderLimits.MaxLines)] IReadOnlyList<ReceiptLineBody> Lines,
    [StringLength(OrderLimits.ReasonLength)] string? DeliveryNote)
{
    internal ReceiveGoods ToCommand(Guid orderId, uint expectedVersion) =>
        new(orderId, expectedVersion, [.. Lines.Select(l => new ReceiptLineInput(l.LineNo, l.Quantity))], DeliveryNote);
}

/// <summary>Why the rest of the order will not come.</summary>
public sealed record CloseShortBody([Required, StringLength(OrderLimits.ReasonLength, MinimumLength = OrderLimits.MinReasonLength)] string Reason)
{
    internal CloseOrderShort ToCommand(Guid orderId, uint expectedVersion) => new(orderId, expectedVersion, Reason.Trim());
}

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class OrderLimits
{
    public const int MaxLines = 100;
    public const int MaxQuantity = 100_000;
    public const int MinReasonLength = 3;
    public const int ReasonLength = 500;
    public const int StatusLength = 30;
}
