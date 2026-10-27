using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Application.Mapping;

/// <summary>Domain to DTO and to the integration events other services consume. By hand: money and numbers are value objects.</summary>
public static class PurchasingMapper
{
    public static PurchaseOrderDto ToDto(PurchaseOrder order) => new(
        order.Id, order.RequestId, NumberText(order), order.RequestNumber, order.RequesterId, order.CostCenterCode, order.SupplierId,
        order.Status, SupplierStatusText.For(order), order.SupplierReference, order.ExpectedDelivery,
        [.. order.Lines.OrderBy(l => l.LineNo).Select(ToLineDto)],
        [.. order.Receipts.OrderBy(r => r.ReceivedAt).Select(ToReceiptDto)],
        order.TotalExclVat.Amount, order.Vat.Amount, order.TotalInclVat.Amount,
        order.TotalExclVat.ToDisplayString(), order.Vat.ToDisplayString(), order.TotalInclVat.ToDisplayString(),
        order.CreatedAt, order.CancelReason, order.CancelledAt, order.Version);

    public static OrderLineDto ToLineDto(OrderLine line) => new(
        line.LineNo, line.Sku, line.Description, line.Quantity, line.Received, line.ClosedShort, line.Outstanding,
        line.UnitPriceExclVat.Amount, line.LineTotalExclVat.Amount, line.UnitPriceExclVat.ToDisplayString(),
        line.LineTotalExclVat.ToDisplayString());

    public static GoodsReceiptDto ToReceiptDto(GoodsReceipt receipt) => new(
        receipt.Id, receipt.ReceivedBy, receipt.ReceivedAt, receipt.DeliveryNote,
        [.. receipt.Lines.Select(l => new ReceiptLineDto(l.LineNo, l.Quantity))]);

    public static PurchaseOrderCreated ToCreatedEvent(PurchaseOrderCreatedDomainEvent created)
    {
        var order = created.Order;
        return new PurchaseOrderCreated(
            order.Id, order.RequestId, order.TenantId, NumberText(order), order.RequestNumber, order.SupplierId, order.CostCenterCode,
            order.RequesterId,
            [.. order.Lines.OrderBy(l => l.LineNo).Select(l => new OrderLineItem(l.LineNo, l.Sku, l.Description, l.Quantity, l.UnitPriceExclVat.Amount))],
            order.TotalExclVat.Amount, order.Vat.Amount, order.TotalInclVat.Amount, created.OccurredAt);
    }

    public static PurchaseOrderConfirmed ToConfirmedEvent(PurchaseOrder order, DateTimeOffset now) => new(
        order.Id, order.RequestId, order.TenantId, NumberText(order), order.SupplierReference!, order.ExpectedDelivery, now);

    public static PurchaseOrderRejected ToRejectedEvent(PurchaseOrder order, string reason, DateTimeOffset now) => new(
        order.Id, order.RequestId, order.TenantId, NumberText(order), reason, now);

    public static GoodsReceived ToGoodsReceivedEvent(PurchaseOrder order, GoodsReceipt receipt) => new(
        order.Id, order.RequestId, order.TenantId, receipt.Id,
        [.. receipt.Lines.Select(l => new ReceivedLine(l.LineNo, l.Quantity))], receipt.ReceivedBy, receipt.ReceivedAt);

    public static PurchaseOrderClosedShort ToClosedShortEvent(
        PurchaseOrder order, IReadOnlyList<(int LineNo, int Quantity)> closed, string reason, string closedBy, DateTimeOffset now) => new(
        order.Id, order.RequestId, order.TenantId, [.. closed.Select(c => new ReceivedLine(c.LineNo, c.Quantity))], reason, closedBy, now);

    /// <summary>The number as text; a tombstone has none.</summary>
    public static string NumberText(PurchaseOrder order) => order.Number?.ToString() ?? "";
}

/// <summary>What the supplier side of an order looks like to a person, in one line.</summary>
public static class SupplierStatusText
{
    public static string For(PurchaseOrder order) => order.Status switch
    {
        OrderStatus.PendingSupplier when order.SupplierAttempts > 0 =>
            $"Supplier unavailable · retrying (attempt {order.SupplierAttempts + 1})",
        OrderStatus.PendingSupplier => "Waiting for supplier",
        OrderStatus.SentToSupplier => "Sent to supplier, waiting for confirmation",
        OrderStatus.Rejected => string.IsNullOrWhiteSpace(order.CancelReason)
            ? "Rejected by supplier"
            : $"Rejected by supplier: {order.CancelReason}",
        OrderStatus.Cancelled when order.SupplierCancelPending => "Cancelled, telling the supplier",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Confirmed or OrderStatus.PartiallyReceived or OrderStatus.Received or OrderStatus.ClosedShort =>
            order.ExpectedDelivery is { } date
                ? $"Confirmed, delivery {date:yyyy-MM-dd}"
                : "Confirmed",
        _ => order.Status.ToString(),
    };
}
