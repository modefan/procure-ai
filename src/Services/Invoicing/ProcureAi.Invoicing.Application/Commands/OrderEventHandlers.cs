using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
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

    /// <summary>Evicts the cached reads of every invoice of <paramref name="order"/> (and the tenant's list) once the handler commits.</summary>
    public static async Task EvictInvoicesOfAsync(IInvoicingDbContext db, ICacheInvalidator cache, OrderView order, CancellationToken cancellationToken)
    {
        var invoiceIds = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.TenantId == order.TenantId && i.OrderId == order.Id)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(order.TenantId, invoiceIds));
    }

    /// <summary>The invoices of both lists, each once (an invoice adopted in this unit of work is not in the database's answer yet).</summary>
    public static List<Invoice> Union(IEnumerable<Invoice> loaded, IEnumerable<Invoice> more) => [.. loaded.Concat(more).DistinctBy(i => i.Id)];

    /// <summary>
    /// Gives the invoices that wait for <paramref name="order"/> without a tenant (they came by webhook before the order) the order's
    /// tenant and their arrival numbers in it, in the order they arrived. A waiting invoice whose number the tenant already has for this
    /// supplier (a second copy that came by webhook before the order was known) is the supplier's mistake: it is removed and logged,
    /// as it would have been refused had the tenant been known. So are the waiting invoices of an order that is dead by now
    /// (cancelled or rejected): a new invoice for it is refused (<c>RecordInvoice</c>). Returns the adopted invoices (tracked, not yet saved).
    /// </summary>
    public static async Task<List<Invoice>> AdoptWaitingAsync(
        IInvoicingDbContext db, ITenantSequence sequence, OrderView order, ILogger logger, CancellationToken cancellationToken)
    {
        var waiting = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines)
            .Where(i => i.OrderId == order.Id && i.TenantId == Invoice.UnassignedTenant)
            .OrderBy(i => i.ReceivedAt).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        if (order.ClosedReason is { } closed)
        {
            foreach (var invoice in waiting)
            {
                logger.LogWarning(
                    "Invoice {InvoiceId} ({Number}) waited for order {OrderId}, but {Reason} It takes no invoices; removed",
                    invoice.Id, invoice.SupplierInvoiceNumber, order.Id, closed);
                db.Invoices.Remove(invoice);
            }

            return [];
        }

        var adopted = new List<Invoice>();
        foreach (var invoice in waiting)
        {
            var supplier = invoice.SupplierId.Length == 0 ? order.SupplierId : invoice.SupplierId;
            var taken = adopted.Any(a => a.SupplierId == supplier && a.SupplierInvoiceNumber == invoice.SupplierInvoiceNumber)
                || await db.Invoices.IgnoreQueryFilters().AnyAsync(
                    i => i.TenantId == order.TenantId && i.SupplierId == supplier && i.SupplierInvoiceNumber == invoice.SupplierInvoiceNumber,
                    cancellationToken);
            if (taken)
            {
                logger.LogWarning(
                    "Invoice {InvoiceId} waited for order {OrderId}, but the tenant already has invoice number {Number} of {Supplier}; removed",
                    invoice.Id, order.Id, invoice.SupplierInvoiceNumber, supplier);
                db.Invoices.Remove(invoice);
                continue;
            }

            invoice.AssignTenant(order.TenantId, await sequence.NextAsync(order.TenantId, "invoice", cancellationToken));
            adopted.Add(invoice);
        }

        return adopted;
    }

    /// <summary>
    /// Publishes what a re-match changed: <c>InvoiceReceived</c> for invoices that were waiting for the order, then the outcome. Each
    /// new mismatch is counted.
    /// </summary>
    public static async Task PublishAsync(IMessageBus bus, IInvoicingMetrics metrics, IReadOnlyList<MatchOutcome> outcomes, DateTimeOffset now)
    {
        foreach (var outcome in outcomes.Where(o => o.Changed))
        {
            if (outcome.WasAwaitingOrder)
            {
                await bus.PublishAsync(InvoicingMapper.ToReceivedEvent(outcome.Invoice, now));
            }

            if (outcome.Invoice.Status == InvoiceStatus.Mismatched)
            {
                metrics.InvoiceMismatched(outcome.Invoice.Source);
            }

            await bus.PublishAsync(InvoicingMapper.ToOutcomeEvent(outcome.Invoice, now));
        }
    }
}

/// <summary>Keeps a copy of each new order and matches the invoices that arrived before it.</summary>
public static class PurchaseOrderCreatedHandler
{
    /// <summary>
    /// A copy that exists already means a repeat: nothing happens. Otherwise the copy is stored, the invoices that came by webhook
    /// before it get the order's tenant (<see cref="OrderEvents.AdoptWaitingAsync"/>) and all of the order's invoices are matched in
    /// arrival order, with the outcomes published together with the save.
    /// </summary>
    public static async Task Handle(
        PurchaseOrderCreated message, IInvoicingDbContext db, ITenantSequence sequence, IMessageBus bus, ICacheInvalidator cache,
        IInvoicingMetrics metrics, IOptions<InvoicingOptions> options, TimeProvider time, ILogger<PurchaseOrderCreated> logger,
        CancellationToken cancellationToken)
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

        var adopted = await OrderEvents.AdoptWaitingAsync(db, sequence, order, logger, cancellationToken);
        var invoices = await OrderEvents.LoadInvoicesAsync(db, message.TenantId, message.OrderId, cancellationToken);
        var outcomes = MatchDecisions.Rematch(order, OrderEvents.Union(invoices, adopted), Money.Zar(options.Value.PriceTolerance));
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(message.TenantId, outcomes.Select(o => o.Invoice.Id)));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, metrics, outcomes, time.GetUtcNow());
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
        GoodsReceived message, IInvoicingDbContext db, IMessageBus bus, ICacheInvalidator cache, IInvoicingMetrics metrics,
        IOptions<InvoicingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, message.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(message.OrderId);
        order.ApplyReceipt([.. message.Lines.Select(l => (l.LineNo, l.Quantity))]);

        var invoices = await OrderEvents.LoadInvoicesAsync(db, message.TenantId, message.OrderId, cancellationToken);
        var outcomes = MatchDecisions.Rematch(order, invoices, Money.Zar(options.Value.PriceTolerance));
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(message.TenantId, outcomes.Select(o => o.Invoice.Id)));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, metrics, outcomes, time.GetUtcNow());
    }
}

/// <summary>An order closed with goods outstanding: those quantities will never be paid, which can settle the order.</summary>
public static class PurchaseOrderClosedShortHandler
{
    /// <summary>Without the copy of the order yet, <see cref="OrderViewMissingException"/> sends the message back for a later retry.</summary>
    public static async Task Handle(
        PurchaseOrderClosedShort message, IInvoicingDbContext db, IMessageBus bus, ICacheInvalidator cache, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, message.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(message.OrderId);
        order.ApplyClosedShort([.. message.ClosedLines.Select(l => (l.LineNo, l.Quantity))]);
        var now = time.GetUtcNow();
        var settled = order.MarkSettled(now);
        await OrderEvents.EvictInvoicesOfAsync(db, cache, order, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (settled)
        {
            await bus.PublishAsync(InvoicingMapper.ToSettledEvent(order, now));
        }
    }
}

/// <summary>The order was cancelled: it is dead here too (no more matches, invoices or payments).</summary>
public static class PurchaseOrderCancelledHandler
{
    /// <summary>
    /// A cancel before the order existed (no order ID: Purchasing never created it, so no <c>PurchaseOrderCreated</c> follows)
    /// changes nothing. A cancel of an order whose copy has not arrived yet (it overtook <c>PurchaseOrderCreated</c>) throws
    /// <see cref="OrderViewMissingException"/> and is retried until the copy exists, so it is never lost.
    /// </summary>
    public static async Task Handle(
        PurchaseOrderCancelled message, IInvoicingDbContext db, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        if (message.OrderId is not { } orderId)
        {
            return;
        }

        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, orderId, cancellationToken)
            ?? throw new OrderViewMissingException(orderId);
        if (order.Cancel())
        {
            await OrderEvents.EvictInvoicesOfAsync(db, cache, order, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>The supplier rejected the order: it is dead here too (no more matches, invoices or payments).</summary>
public static class PurchaseOrderRejectedHandler
{
    /// <summary>Without the copy of the order yet, <see cref="OrderViewMissingException"/> sends the message back for a later retry.</summary>
    public static async Task Handle(
        PurchaseOrderRejected message, IInvoicingDbContext db, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        var order = await OrderEvents.LoadViewAsync(db, message.TenantId, message.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(message.OrderId);
        if (order.Reject())
        {
            await OrderEvents.EvictInvoicesOfAsync(db, cache, order, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Purchasing events that need nothing from Invoicing. They are consumed so they do not pile up unhandled.</summary>
public static class PurchaseOrderIgnoredEventsHandler
{
    public static void Handle(PurchaseOrderConfirmed message)
    {
    }

    public static void Handle(PurchaseOrderCancellationRefused message)
    {
    }
}

/// <summary>Builds what the ERP is told about a payment.</summary>
public static class ErpPayments
{
    /// <summary>The vendor is always the supplier of the order, never the one named on the invoice.</summary>
    public static ErpPayment Build(Invoice invoice, Payment payment, OrderView order) => new(
        payment.Id, payment.Number.ToString(), order.SupplierId, invoice.SupplierInvoiceNumber, payment.TotalInclVat,
        [.. payment.Lines.Select(l => new ErpPaymentLine(l.LineNo, l.Sku, l.Quantity, l.AmountExclVat))]);
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
        IInvoicingMetrics metrics, IOptions<InvoicingOptions> options, TimeProvider time, ILogger<ExportPayment> logger,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Payments.Any(p => p.Id == command.PaymentId), cancellationToken);
        var payment = invoice?.Payments.FirstOrDefault(p => p.Id == command.PaymentId);
        if (invoice is null || payment is not { ExportStatus: ExportStatus.Pending })
        {
            return;
        }

        var order = await db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == invoice.TenantId && o.Id == invoice.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(invoice.OrderId);
        var erpPayment = ErpPayments.Build(invoice, payment, order);
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        string reference;
        try
        {
            reference = await gateway.ExportPaymentAsync(erpPayment, cancellationToken);
        }
        catch (ErpUnavailableException exception)
        {
            metrics.ErpExportFailed("unavailable");
            var delay = options.Value.ErpExport.DelayFor(command.Attempt, Random.Shared);
            payment.RecordExportFailure(exception.Message);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(exception, "ERP unavailable for payment {PaymentId}, attempt {Attempt}; retrying in {Delay}", payment.Id, command.Attempt, delay);
            await bus.ScheduleAsync(new ExportPayment(payment.Id, command.Attempt + 1), delay);
            return;
        }
        catch (ErpRejectedException exception)
        {
            metrics.ErpExportFailed("rejected");
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
