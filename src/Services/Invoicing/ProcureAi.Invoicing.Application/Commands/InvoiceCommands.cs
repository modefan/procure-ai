using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Application.Mapping;
using ProcureAi.Invoicing.Domain;
using Wolverine;

namespace ProcureAi.Invoicing.Application.Commands;

/// <summary>An invoice line as entered or sent. The SKU is optional: when missing it is taken from the order line with the same number.</summary>
public sealed record InvoiceLineInput(int LineNo, int Quantity, decimal UnitPriceExclVat, string? Sku = null, string? Description = null);

public sealed record PayLineInput(int LineNo, int Quantity);

/// <summary>
/// Records an invoice from the supplier webhook, a manual entry or a scan. <c>SupplierId</c> null means the supplier of the order.
/// <c>Document</c> is the raw body or entry as JSON; for a scan (<c>ScanId</c>, source <c>Scan</c>) the handler replaces it with the scan
/// the Assistant stored. <c>WebhookEventId</c> (webhooks only) makes a replay of the same event do nothing.
/// </summary>
public sealed record RecordInvoice(
    Guid OrderId,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    IReadOnlyList<InvoiceLineInput> Lines,
    decimal StatedTotalInclVat,
    InvoiceSource Source,
    string Document,
    string? SupplierId,
    string? WebhookEventId = null,
    Guid? ScanId = null);

/// <summary>Finance pays the payable quantities of an invoice (<c>Lines</c> null: all of them, or chosen quantities per line).</summary>
public sealed record ReleasePayment(Guid InvoiceId, uint ExpectedVersion, IReadOnlyList<PayLineInput>? Lines);

public sealed record HoldInvoice(Guid InvoiceId, uint ExpectedVersion, string Reason);

public sealed record ReleaseHold(Guid InvoiceId, uint ExpectedVersion);

/// <summary>Local command: hand a payment to the ERP. <c>Attempt</c> counts from 1.</summary>
public sealed record ExportPayment(Guid PaymentId, int Attempt);

/// <summary>
/// Local command: the invoices that came by webhook for <c>OrderId</c> before Invoicing knew the order (no tenant yet) are given the
/// order's tenant and matched. Sent by <see cref="RecordInvoiceHandler"/> when it stores such an invoice, so an invoice that raced
/// <c>PurchaseOrderCreated</c> (whose handler looked for waiting invoices just before this one committed) is still adopted. Without
/// the order view yet it throws <see cref="OrderViewMissingException"/> and is retried on the schedule of <c>Invoicing:OrderViewRetry</c>.
/// </summary>
public sealed record AttachWaitingInvoices(Guid OrderId);

public static class RecordInvoiceHandler
{
    public const string WebhookSource = "supplier";

    /// <summary>The answer when a scan is unknown, expired or of another tenant (a 422 for the caller).</summary>
    public const string ScanNotFound = "The scan was not found.";

    /// <summary>
    /// The order view decides the tenant, never the payload or the caller of a webhook. A webhook (signature checked by the Api) is
    /// recorded once per event ID (a replay changes nothing and returns null). A manual entry or scan needs an order the caller may
    /// see in their own tenant (else 404). The same supplier invoice number again (same tenant and supplier) is the same invoice: same
    /// content returns the stored invoice, other content is a conflict (409). A webhook invoice for an order not known yet is kept
    /// without a tenant (<see cref="InvoiceStatus.AwaitingOrder"/>, nothing published, invisible to every tenant) and adopted when
    /// the order arrives (<see cref="AttachWaitingInvoices"/>). Otherwise it is matched at once and <c>InvoiceReceived</c> and
    /// <c>InvoiceMatched</c> or <c>InvoiceMismatched</c> go out with the save. The order view is written too, so concurrent invoices
    /// and payments of one order never overlap; a lost race is retried by the concurrency policy. An entry with a scan (<c>ScanId</c>) reads
    /// it from the Assistant for the caller's tenant (<see cref="IAssistantScans"/>) and keeps it as the invoice's document (source
    /// <c>Scan</c>); a scan the Assistant does not have for that tenant, or hands over as another tenant's, is a 422. The lines are always
    /// the command's (finance's reviewed values), never the scan's.
    /// </summary>
    public static async Task<InvoiceDto?> Handle(
        RecordInvoice command, IInvoicingDbContext db, ICallerContext caller, ICallerScope scope, IWebhookInbox inbox,
        ITenantSequence sequence, IMessageBus bus, ICacheInvalidator cache, IInvoicingMetrics metrics, IOptions<InvoicingOptions> options,
        IAssistantScans scans, TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);

        if (command.Source == InvoiceSource.Webhook)
        {
            if (string.IsNullOrWhiteSpace(command.WebhookEventId))
            {
                throw new DomainException("A supplier invoice needs the ID of its webhook event.");
            }

            if (!await inbox.TryRecordAsync(WebhookSource, command.WebhookEventId, cancellationToken))
            {
                return null;
            }
        }
        else
        {
            var callerTenant = caller.TenantId;
            if (order is null || callerTenant is null || order.TenantId != callerTenant
                || !(await scope.GetAsync(cancellationToken)).Allows(order.RequesterId, order.CostCenterCode))
            {
                throw new NotFoundException($"Order {command.OrderId} was not found.");
            }

            if (command.ScanId is { } scanId)
            {
                command = await WithScanAsync(command, scanId, callerTenant, scans, cancellationToken);
            }
        }

        var tenant = order?.TenantId ?? Invoice.UnassignedTenant;
        var supplier = string.IsNullOrWhiteSpace(command.SupplierId) ? order?.SupplierId ?? "" : command.SupplierId.Trim();
        var number = command.SupplierInvoiceNumber?.Trim() ?? "";
        var lines = BuildLines(command.Lines, order);
        var stated = Money.Zar(command.StatedTotalInclVat);

        // Without a tenant the number is only known among the invoices waiting for the same order.
        var existing = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(
                i => i.TenantId == tenant && i.SupplierId == supplier && i.SupplierInvoiceNumber == number
                    && (tenant != Invoice.UnassignedTenant || i.OrderId == command.OrderId),
                cancellationToken);
        if (existing is not null)
        {
            return existing.HasSameContentAs(command.OrderId, command.InvoiceDate, lines, stated)
                ? InvoicingMapper.ToDto(existing, order)
                : throw new ConflictException($"Invoice {number} of {supplier} is already recorded with other content.");
        }

        var now = time.GetUtcNow();
        if (order is null)
        {
            var waiting = Invoice.CreateAwaitingOrder(
                command.OrderId, supplier, number, command.InvoiceDate, lines, stated, command.Document, now);
            db.Invoices.Add(waiting);
            await db.SaveChangesAsync(cancellationToken);
            await bus.SendAsync(new AttachWaitingInvoices(command.OrderId));
            return InvoicingMapper.ToDto(waiting, null);
        }

        if (order.ClosedReason is { } closed)
        {
            // A dead order (cancelled or rejected) takes no invoices: nothing can be received or paid against it.
            throw new DomainException($"{closed} It takes no invoices.");
        }

        var invoice = Invoice.Create(
            tenant, command.OrderId, order.RequestId, supplier, number, command.InvoiceDate,
            await sequence.NextAsync(tenant, "invoice", cancellationToken), command.Source, lines, stated, command.Document, now);
        db.Invoices.Add(invoice);

        var earlier = await OrderEvents.LoadInvoicesAsync(db, tenant, order.Id, cancellationToken);
        var outcome = MatchDecisions.Rematch(order, OrderEvents.Union(earlier, [invoice]), Money.Zar(options.Value.PriceTolerance), only: invoice).Single();

        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(tenant, invoice.Id));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, metrics, [outcome with { WasAwaitingOrder = true }], now);
        return InvoicingMapper.ToDto(invoice, order);
    }

    /// <summary>The command as an entry from the scan: source Scan, the scan the Assistant stored for the tenant as its document.</summary>
    private static async Task<RecordInvoice> WithScanAsync(
        RecordInvoice command, Guid scanId, string tenant, IAssistantScans scans, CancellationToken cancellationToken)
    {
        var scan = await scans.GetAsync(tenant, scanId, cancellationToken);
        if (scan is null || scan.Id != scanId || !string.Equals(scan.TenantId, tenant, StringComparison.Ordinal))
        {
            throw new DomainException(ScanNotFound);
        }

        return command with { Source = InvoiceSource.Scan, Document = scan.Json };
    }

    private static List<InvoiceLine> BuildLines(IReadOnlyList<InvoiceLineInput> inputs, OrderView? order) =>
    [
        .. (inputs ?? []).Select(l => new InvoiceLine(
            l.LineNo,
            l.Sku ?? order?.Lines.FirstOrDefault(o => o.LineNo == l.LineNo)?.Sku ?? "",
            l.Description ?? order?.Lines.FirstOrDefault(o => o.LineNo == l.LineNo)?.Description ?? "",
            l.Quantity,
            Money.Zar(l.UnitPriceExclVat))),
    ];
}

public static class AttachWaitingInvoicesHandler
{
    /// <summary>Nothing waiting: done (the order's own handler adopted them, or this is a repeat). Waiting but no order view yet: retried.</summary>
    public static async Task Handle(
        AttachWaitingInvoices command, IInvoicingDbContext db, ITenantSequence sequence, IMessageBus bus, ICacheInvalidator cache,
        IInvoicingMetrics metrics, IOptions<InvoicingOptions> options, TimeProvider time, ILogger<AttachWaitingInvoices> logger,
        CancellationToken cancellationToken)
    {
        var waiting = await db.Invoices.IgnoreQueryFilters()
            .AnyAsync(i => i.OrderId == command.OrderId && i.TenantId == Invoice.UnassignedTenant, cancellationToken);
        if (!waiting)
        {
            return;
        }

        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken)
            ?? throw new OrderViewMissingException(command.OrderId);
        var adopted = await OrderEvents.AdoptWaitingAsync(db, sequence, order, logger, cancellationToken);
        var invoices = await OrderEvents.LoadInvoicesAsync(db, order.TenantId, order.Id, cancellationToken);
        var outcomes = MatchDecisions.Rematch(order, OrderEvents.Union(invoices, adopted), Money.Zar(options.Value.PriceTolerance));
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(order.TenantId, outcomes.Select(o => o.Invoice.Id)));
        await db.SaveChangesAsync(cancellationToken);
        await OrderEvents.PublishAsync(bus, metrics, outcomes, time.GetUtcNow());
    }
}

/// <summary>Loading an invoice and its order for a caller: missing and out-of-scope are both "not found".</summary>
internal static class InvoiceAccess
{
    /// <summary>The tenant filter applies; an invoice of the tenant always has its order view (only webhooks wait, without a tenant).</summary>
    public static async Task<(Invoice Invoice, OrderView Order)> LoadAllowedAsync(
        IInvoicingDbContext db, ICallerScope scope, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new NotFoundException($"Invoice {invoiceId} was not found.");
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == invoice.OrderId, cancellationToken);
        if (order is null || !(await scope.GetAsync(cancellationToken)).Allows(order.RequesterId, order.CostCenterCode))
        {
            throw new NotFoundException($"Invoice {invoiceId} was not found.");
        }

        return (invoice, order);
    }
}

public static class ReleasePaymentHandler
{
    /// <summary>
    /// Out of scope is 404. The requester of the request cannot release its payment (403, separation of duties). A stale version
    /// is a 412, checked before the domain rules. The payment number comes from the tenant sequence <c>payment</c>. The invoice,
    /// the payment and the order view (paid quantities, settled) are saved together, with <c>PaymentReleased</c> and, when the
    /// order became settled, <c>PurchaseOrderSettled</c>; the export to the ERP is queued as a local command.
    /// </summary>
    public static async Task<InvoiceDto> Handle(
        ReleasePayment command, IInvoicingDbContext db, ICallerContext caller, ICallerScope scope, ITenantSequence sequence,
        IMessageBus bus, ICacheInvalidator cache, TimeProvider time, CancellationToken cancellationToken)
    {
        var (invoice, order) = await InvoiceAccess.LoadAllowedAsync(db, scope, command.InvoiceId, cancellationToken);
        if (string.Equals(order.RequesterId, caller.UserId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("You raised the request of this order, so someone else must release its payment.");
        }

        OptimisticConcurrency.EnsureVersion($"Invoice {command.InvoiceId}", invoice.Version, command.ExpectedVersion);
        var now = time.GetUtcNow();
        var plan = invoice.PlanPayment(order, command.Lines?.Select(l => (l.LineNo, l.Quantity)).ToList());
        var number = PaymentNumber.FromSequence(await sequence.NextAsync(invoice.TenantId, PaymentNumber.SequenceName, cancellationToken));
        var payment = invoice.Pay(plan, number, caller.UserId, now);
        var settled = order.MarkSettled(now);

        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(InvoicingMapper.ToReleasedEvent(invoice, payment, now));
        if (settled)
        {
            await bus.PublishAsync(InvoicingMapper.ToSettledEvent(order, now));
        }

        await bus.SendAsync(new ExportPayment(payment.Id, 1));
        return InvoicingMapper.ToDto(invoice, order);
    }
}

public static class HoldInvoiceHandler
{
    /// <summary>Puts the invoice on hold (no payment until released). Same access and version rules as paying.</summary>
    public static async Task<InvoiceDto> Handle(
        HoldInvoice command, IInvoicingDbContext db, ICallerScope scope, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        var (invoice, order) = await InvoiceAccess.LoadAllowedAsync(db, scope, command.InvoiceId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Invoice {command.InvoiceId}", invoice.Version, command.ExpectedVersion);
        invoice.Hold(command.Reason);
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        await db.SaveChangesAsync(cancellationToken);
        return InvoicingMapper.ToDto(invoice, order);
    }
}

public static class ReleaseHoldHandler
{
    /// <summary>
    /// Takes the invoice off hold and matches it again, because goods may have arrived while it was held (events are published
    /// only when the outcome changed).
    /// </summary>
    public static async Task<InvoiceDto> Handle(
        ReleaseHold command, IInvoicingDbContext db, ICallerScope scope, IMessageBus bus, ICacheInvalidator cache,
        IInvoicingMetrics metrics, IOptions<InvoicingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var (invoice, order) = await InvoiceAccess.LoadAllowedAsync(db, scope, command.InvoiceId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Invoice {command.InvoiceId}", invoice.Version, command.ExpectedVersion);
        invoice.ReleaseHold();
        var now = time.GetUtcNow();
        MatchOutcome? outcome = null;
        if (invoice.Status is InvoiceStatus.Matched or InvoiceStatus.Mismatched)
        {
            var all = await db.Invoices.Include(i => i.Lines).Where(i => i.OrderId == order.Id).ToListAsync(cancellationToken);
            outcome = MatchDecisions.Rematch(order, all, Money.Zar(options.Value.PriceTolerance), only: invoice).SingleOrDefault();
        }

        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        await db.SaveChangesAsync(cancellationToken);
        if (outcome is not null)
        {
            await OrderEvents.PublishAsync(bus, metrics, [outcome], now);
        }

        return InvoicingMapper.ToDto(invoice, order);
    }
}
