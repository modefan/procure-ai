using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Application.Mapping;
using ProcureAi.Invoicing.Domain;
using Wolverine;

namespace ProcureAi.Invoicing.Application.Commands;

/// <summary>Shared by the handlers of Purchasing's events: they run for a tenant given in the message, not for a caller.</summary>
internal static class OrderEvents
{
    public static Task<OrderView> LoadViewAsync(IInvoicingDbContext db, string tenantId, Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken)!;

    public static Task<List<Invoice>> LoadInvoicesAsync(IInvoicingDbContext db, string tenantId, Guid orderId, CancellationToken cancellationToken) =>
        db.Invoices.IgnoreQueryFilters().Include(i => i.Lines)
            .Where(i => i.TenantId == tenantId && i.OrderId == orderId)
            .ToListAsync(cancellationToken);

    /// <summary>Publishes what a re-match changed: <c>InvoiceReceived</c> for invoices that were waiting for the order, then the outcome.</summary>
    public static async Task PublishAsync(IMessageBus bus, IReadOnlyList<MatchOutcome> outcomes, DateTimeOffset now)
    {
        foreach (var outcome in outcomes.Where(o => o.Changed))
        {
            if (outcome.WasAwaitingOrder)
            {
                await bus.PublishAsync(InvoicingMapper.ToReceivedEvent(outcome.Invoice, now));
            }

            await bus.PublishAsync(InvoicingMapper.ToOutcomeEvent(outcome.Invoice, now));
        }
    }
}

/// <summary>Keeps a copy of each new order and matches the invoices that arrived before it.</summary>
public static class PurchaseOrderCreatedHandler
{
    /// <summary>
    /// A copy that exists already means a repeat: nothing happens. Otherwise the copy is stored and the invoices waiting for this
    /// order are matched in arrival order, with the outcomes published together with the save.
    /// </summary>
    public static async Task Handle(
        PurchaseOrderCreated message, IInvoicingDbContext db, IMessageBus bus, ICacheInvalidator cache,
        IOptions<InvoicingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var exists = await db.Orders.IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == message.TenantId && o.Id == message.OrderId, cancellationToken);
        if (exists)
        {
            return;
        }

        var order = OrderView.Create(
            message.OrderId, message.TenantId, message.RequestId, message.OrderNumber, message.SupplierId, message.CostCenterCode,
            message.RequesterId,
            [.. message.Lines.Select(l => new OrderViewLine(l.LineNo, l.Sku, l.Description, l.Quantity, Money.Zar(l.UnitPriceExclVat)))]);
        db.Orders.Add(order);

        var invoices = await OrderEvents.LoadInvoicesAsync(db, message.TenantId, message.OrderId, cancellationToken);
        var outcomes = MatchDecisions.Rematch(order, invoices, Money.Zar(options.Value.PriceTolerance));
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(message.TenantId, outcomes.Select(o => o.Invoice.Id)));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, outcomes, time.GetUtcNow());
    }
}

/// <summary>Applies a delivery to the copy of the order and matches the order's unpaid invoices again.</summary>
public static class GoodsReceivedHandler
{
    /// <summary>
    /// The unpaid invoices that are not on hold are matched again in arrival order; only invoices whose outcome changed are
    /// published. Without the copy of the order yet, <see cref="OrderViewMissingException"/> sends the message back for a later retry.
    /// </summary>
    public static async Task Handle(
        GoodsReceived message, IInvoicingDbContext db, IMessageBus bus, ICacheInvalidator cache,
        IOptions<InvoicingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, message.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(message.OrderId);
        order.ApplyReceipt([.. message.Lines.Select(l => (l.LineNo, l.Quantity))]);

        var invoices = await OrderEvents.LoadInvoicesAsync(db, message.TenantId, message.OrderId, cancellationToken);
        var outcomes = MatchDecisions.Rematch(order, invoices, Money.Zar(options.Value.PriceTolerance));
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(message.TenantId, outcomes.Select(o => o.Invoice.Id)));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, outcomes, time.GetUtcNow());
    }
}

/// <summary>An order closed with goods outstanding: those quantities will never be paid, which can settle the order.</summary>
public static class PurchaseOrderClosedShortHandler
{
    public static async Task Handle(
        PurchaseOrderClosedShort message, IInvoicingDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, message.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(message.OrderId);
        order.ApplyClosedShort([.. message.ClosedLines.Select(l => (l.LineNo, l.Quantity))]);
        var now = time.GetUtcNow();
        var settled = order.MarkSettled(now);
        await db.SaveChangesAsync(cancellationToken);
        if (settled)
        {
            await bus.PublishAsync(InvoicingMapper.ToSettledEvent(order, now));
        }
    }
}

public static class PurchaseOrderCancelledHandler
{
    /// <summary>A cancel before the order existed (no order ID) or for an order never copied here changes nothing.</summary>
    public static async Task Handle(
        PurchaseOrderCancelled message, IInvoicingDbContext db, ILogger<PurchaseOrderCancelled> logger, CancellationToken cancellationToken)
    {
        if (message.OrderId is not { } orderId)
        {
            return;
        }

        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, orderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} was cancelled but Invoicing has no copy of it; ignored", orderId);
            return;
        }

        order.Cancel();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Purchasing events that need nothing from Invoicing. They are consumed so they do not pile up unhandled.</summary>
public static class PurchaseOrderIgnoredEventsHandler
{
    public static void Handle(PurchaseOrderConfirmed message)
    {
    }

    public static void Handle(PurchaseOrderRejected message)
    {
    }

    public static void Handle(PurchaseOrderCancellationRefused message)
    {
    }
}

/// <summary>Hands a released payment to the ERP.</summary>
public static class ExportPaymentHandler
{
    /// <summary>
    /// For a payment not yet exported. ERP unavailable: the failure is stored on the payment and the next attempt scheduled with
    /// backoff (<c>Invoicing:ErpExport</c>), without end. The ERP refuses it for good (400): the error is stored and the export stops
    /// (people see it on the payment). Accepted: the ERP reference is stored and <c>PaymentExported</c> published.
    /// </summary>
    public static async Task Handle(
        ExportPayment command, IInvoicingDbContext db, IErpGateway gateway, IMessageBus bus, ICacheInvalidator cache,
        IOptions<InvoicingOptions> options, TimeProvider time, ILogger<ExportPayment> logger, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Payments.Any(p => p.Id == command.PaymentId), cancellationToken);
        var payment = invoice?.Payments.FirstOrDefault(p => p.Id == command.PaymentId);
        if (invoice is null || payment is not { ExportStatus: ExportStatus.Pending })
        {
            return;
        }

        var erpPayment = new ErpPayment(
            payment.Id, payment.Number.ToString(), invoice.SupplierId, invoice.SupplierInvoiceNumber, payment.TotalInclVat,
            [.. payment.Lines.Select(l => new ErpPaymentLine(l.LineNo, l.Sku, l.Quantity, l.AmountExclVat))]);
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        string reference;
        try
        {
            reference = await gateway.ExportPaymentAsync(erpPayment, cancellationToken);
        }
        catch (ErpUnavailableException exception)
        {
            var delay = options.Value.ErpExport.DelayFor(command.Attempt, Random.Shared);
            payment.RecordExportFailure(exception.Message);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(exception, "ERP unavailable for payment {PaymentId}, attempt {Attempt}; retrying in {Delay}", payment.Id, command.Attempt, delay);
            await bus.ScheduleAsync(new ExportPayment(payment.Id, command.Attempt + 1), delay);
            return;
        }
        catch (ErpRejectedException exception)
        {
            payment.RecordExportFailure(exception.Message);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "ERP refused payment {PaymentId}; not retried", payment.Id);
            return;
        }

        payment.MarkExported(reference);
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(InvoicingMapper.ToExportedEvent(invoice, payment, time.GetUtcNow()));
    }
}
