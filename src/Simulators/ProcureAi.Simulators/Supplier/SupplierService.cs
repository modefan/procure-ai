using Microsoft.Extensions.Options;
using ProcureAi.Simulators.Webhooks;

namespace ProcureAi.Simulators.Supplier;

/// <summary>What the supplier does with orders: receives them, answers by webhook after a delay, invoices what was ordered.</summary>
public sealed class SupplierService(
    SupplierStore store,
    WebhookQueue webhooks,
    IOptions<SimulatorOptions> options,
    TimeProvider time,
    ILogger<SupplierService> logger)
{
    public const string AcknowledgedType = "order.acknowledged";
    public const string DeclinedType = "order.declined";
    public const string InvoiceIssuedType = "invoice.issued";

    public OrderReceipt Receive(CreateOrderRequest request)
    {
        var (order, created) = store.GetOrAdd(request);
        if (created)
        {
            Plan(order);
            logger.LogInformation("Received order {SupplierOrderId} for {ExternalReference}", order.Id, order.ExternalReference);
        }

        return new OrderReceipt(order.Id, "RECEIVED");
    }

    /// <summary>Cancels an order (idempotent): webhooks still waiting for it are dropped by the sender. Null for an unknown order.</summary>
    public CancelReceipt? Cancel(string supplierOrderId)
    {
        var order = store.Find(supplierOrderId);
        if (order is null)
        {
            return null;
        }

        order.Cancelled = true;
        return new CancelReceipt("CANCELLED");
    }

    /// <summary>Sends an invoice now (admin hook). The error is a message for a 400; null order means unknown.</summary>
    public (string? InvoiceNumber, string? Error) IssueInvoiceNow(SupplierOrder order, IssueInvoiceRequest request)
    {
        var lines = new List<OrderLineDto>();
        var deviation = request.PriceDeviationCents ?? 0;
        if (request.Lines is { Count: > 0 })
        {
            foreach (var requested in request.Lines)
            {
                var ordered = order.Lines.FirstOrDefault(l => l.LineNumber == requested.LineNumber);
                if (ordered is null)
                {
                    return (null, $"The order has no line {requested.LineNumber}.");
                }

                lines.Add(ordered with { Quantity = requested.Quantity, NetPriceCents = ordered.NetPriceCents + deviation });
            }
        }
        else
        {
            lines.AddRange(order.Lines.Select(l => l with { NetPriceCents = l.NetPriceCents + deviation }));
        }

        var number = string.IsNullOrWhiteSpace(request.InvoiceNumber) ? store.NextInvoiceNumber() : request.InvoiceNumber.Trim();
        webhooks.Schedule(order.Id, WebhookTarget.Invoicing, InvoiceIssuedType, time.GetUtcNow(), BuildInvoice(order, number, lines));
        return (number, null);
    }

    private void Plan(SupplierOrder order)
    {
        var supplier = options.Value.Supplier;
        var now = time.GetUtcNow();

        var discontinued = order.Lines.FirstOrDefault(l => l.ArticleNumber.StartsWith(supplier.RejectPrefix, StringComparison.Ordinal));
        if (discontinued is not null)
        {
            var declined = new OrderWebhook(
                Guid.NewGuid(), DeclinedType, order.Id, order.ExternalReference, null, $"Article {discontinued.ArticleNumber} is discontinued");
            webhooks.Schedule(order.Id, WebhookTarget.Purchasing, DeclinedType, now + supplier.ConfirmDelay, declined);
            return;
        }

        var delivery = DateOnly.FromDateTime(now.UtcDateTime).AddDays(supplier.DeliveryDays);
        var acknowledged = new OrderWebhook(Guid.NewGuid(), AcknowledgedType, order.Id, order.ExternalReference, delivery, null);
        webhooks.Schedule(order.Id, WebhookTarget.Purchasing, AcknowledgedType, now + supplier.ConfirmDelay, acknowledged);

        if (supplier.AutoInvoice)
        {
            webhooks.Schedule(
                order.Id, WebhookTarget.Invoicing, InvoiceIssuedType, now + supplier.InvoiceDelay,
                BuildInvoice(order, store.NextInvoiceNumber(), order.Lines));
        }
    }

    private InvoiceWebhook BuildInvoice(SupplierOrder order, string invoiceNumber, IReadOnlyList<OrderLineDto> lines)
    {
        var supplier = options.Value.Supplier;
        var net = lines.Sum(l => l.Quantity * l.NetPriceCents);
        var vat = (long)Math.Round(net * supplier.VatRate, MidpointRounding.AwayFromZero);
        return new InvoiceWebhook(
            Guid.NewGuid(), InvoiceIssuedType, invoiceNumber, supplier.SupplierCode, order.ExternalReference,
            DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), lines, net, vat, net + vat);
    }
}
