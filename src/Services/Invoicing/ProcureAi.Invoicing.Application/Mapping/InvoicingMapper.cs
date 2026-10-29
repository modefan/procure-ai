using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Application.Mapping;

/// <summary>Domain to DTO and to the integration events other services consume. By hand: money and numbers are value objects.</summary>
public static class InvoicingMapper
{
    /// <summary>The invoice with its match lines; <paramref name="order"/> (its order view) adds the ordered quantities and prices.</summary>
    public static InvoiceDto ToDto(Invoice invoice, OrderView? order) => ToDto(invoice, order, order is null ? null : invoice.PlanAllPayable(order));

    private static InvoiceDto ToDto(Invoice invoice, OrderView? order, PaymentPlan? payable) => new(
        invoice.Id, invoice.OrderId, order?.OrderNumber, invoice.RequestId, invoice.SupplierId, invoice.SupplierInvoiceNumber, invoice.InvoiceDate,
        invoice.Sequence, invoice.Source, invoice.Status, invoice.OnHold, invoice.HoldReason,
        invoice.Match?.Reasons ?? [],
        [.. invoice.Lines.OrderBy(l => l.LineNo).Select(l => ToLineDto(
            l, invoice.Match?.Lines.FirstOrDefault(m => m.LineNo == l.LineNo), order?.Lines.FirstOrDefault(o => o.LineNo == l.LineNo),
            payable?.Lines.FirstOrDefault(p => p.LineNo == l.LineNo)?.Quantity ?? 0))],
        [.. invoice.Payments.OrderBy(p => p.ReleasedAt).Select(ToPaymentDto)],
        invoice.TotalExclVat.Amount, invoice.Vat.Amount, invoice.TotalInclVat.Amount,
        invoice.TotalExclVat.ToDisplayString(), invoice.Vat.ToDisplayString(), invoice.TotalInclVat.ToDisplayString(),
        payable?.TotalInclVat.Amount ?? 0m, (payable?.TotalInclVat ?? Money.Zero).ToDisplayString(),
        invoice.ReceivedAt, invoice.Version);

    public static InvoiceLineDto ToLineDto(InvoiceLine line, LineMatch? match, OrderViewLine? orderLine, int payable = 0) =>
        ToLineDtoOf(line, match, SameItem(line, orderLine) ? orderLine : null, payable);

    /// <summary>The order line is the invoice line's own only when it names the same item (as the match decides: "not on the order" otherwise).</summary>
    private static bool SameItem(InvoiceLine line, OrderViewLine? orderLine) =>
        orderLine is not null && (line.Sku.Length == 0 || string.Equals(orderLine.Sku, line.Sku, StringComparison.OrdinalIgnoreCase));

    private static InvoiceLineDto ToLineDtoOf(InvoiceLine line, LineMatch? match, OrderViewLine? orderLine, int payable) => new(
        line.LineNo, line.Sku, line.Description, line.Quantity, line.Paid, line.UnitPriceExclVat.Amount, line.LineTotalExclVat.Amount,
        line.UnitPriceExclVat.ToDisplayString(), line.LineTotalExclVat.ToDisplayString(),
        orderLine?.Ordered, orderLine?.UnitPriceExclVat.Amount, orderLine?.UnitPriceExclVat.ToDisplayString(),
        match?.PriceOk, match?.QuantityOk, match?.Received, match?.InvoicedBefore, match?.Reason, payable);

    public static PaymentDto ToPaymentDto(Payment payment) => new(
        payment.Id, payment.Number.ToString(),
        [.. payment.Lines.Select(l => new PaymentLineDto(l.LineNo, l.Sku, l.Quantity, l.AmountExclVat.Amount, l.AmountExclVat.ToDisplayString()))],
        payment.TotalExclVat.Amount, payment.Vat.Amount, payment.TotalInclVat.Amount, payment.TotalInclVat.ToDisplayString(),
        payment.ReleasedBy, payment.ReleasedAt, payment.ExportStatus, payment.ErpReference, payment.ExportAttempts,
        payment.LastExportError);

    public static InvoiceReceived ToReceivedEvent(Invoice invoice, DateTimeOffset now) => new(
        invoice.Id, invoice.OrderId, invoice.RequestId, invoice.TenantId, invoice.SupplierId, invoice.SupplierInvoiceNumber,
        invoice.Source.ToString(), invoice.StatedTotalInclVat.Amount, now);

    public static InvoiceMatched ToMatchedEvent(Invoice invoice, DateTimeOffset now) => new(
        invoice.Id, invoice.OrderId, invoice.RequestId, invoice.TenantId, now);

    public static InvoiceMismatched ToMismatchedEvent(Invoice invoice, DateTimeOffset now) => new(
        invoice.Id, invoice.OrderId, invoice.RequestId, invoice.TenantId, invoice.Match?.Reasons ?? [], now);

    /// <summary>The outcome event of a match: matched or mismatched.</summary>
    public static object ToOutcomeEvent(Invoice invoice, DateTimeOffset now) =>
        invoice.Status == InvoiceStatus.Matched ? ToMatchedEvent(invoice, now) : ToMismatchedEvent(invoice, now);

    public static PaymentReleased ToReleasedEvent(Invoice invoice, Payment payment, DateTimeOffset now) => new(
        payment.Id, payment.Number.ToString(), invoice.Id, invoice.OrderId, invoice.RequestId, invoice.TenantId,
        payment.TotalInclVat.Amount, [.. payment.Lines.Select(l => new PaidLine(l.LineNo, l.Quantity, l.AmountExclVat.Amount))],
        payment.ReleasedBy, now);

    public static PaymentExported ToExportedEvent(Invoice invoice, Payment payment, DateTimeOffset now) => new(
        payment.Id, invoice.Id, invoice.RequestId, invoice.TenantId, payment.ErpReference!, now);

    public static PurchaseOrderSettled ToSettledEvent(OrderView order, DateTimeOffset now) => new(
        order.Id, order.RequestId, order.TenantId, order.PaidInclVat.Amount, now);
}
