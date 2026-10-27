using Microsoft.EntityFrameworkCore;
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
/// <c>Document</c> is the raw body or entry as JSON. <c>WebhookEventId</c> (webhooks only) makes a replay of the same event do nothing.
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
    string? WebhookEventId = null);

/// <summary>Finance pays the payable quantities of an invoice (<c>Lines</c> null: all of them, or chosen quantities per line).</summary>
public sealed record ReleasePayment(Guid InvoiceId, uint ExpectedVersion, IReadOnlyList<PayLineInput>? Lines);

public sealed record HoldInvoice(Guid InvoiceId, uint ExpectedVersion, string Reason);

public sealed record ReleaseHold(Guid InvoiceId, uint ExpectedVersion);

/// <summary>Local command: hand a payment to the ERP. <c>Attempt</c> counts from 1.</summary>
public sealed record ExportPayment(Guid PaymentId, int Attempt);

public static class RecordInvoiceHandler
{
    public const string WebhookSource = "supplier";

    /// <summary>
    /// The order view decides the tenant. A webhook is trusted (the Api checked its signature); a manual entry or scan is checked
    /// against the caller: same tenant, and the caller may see the order (else 404). The same supplier invoice number again is the
    /// same invoice: same content returns the stored invoice, other content is a conflict (409). Without an order view yet the
    /// invoice waits (<see cref="InvoiceStatus.AwaitingOrder"/>, nothing published) and is matched when the order arrives.
    /// Otherwise it is matched at once and <c>InvoiceReceived</c> and <c>InvoiceMatched</c> or <c>InvoiceMismatched</c> go out with
    /// the save. The order view is written too, so concurrent invoices and payments of one order never overlap.
    /// </summary>
    public static async Task<InvoiceDto> Handle(
        RecordInvoice command, IInvoicingDbContext db, ICallerContext caller, ICallerScope scope, IWebhookInbox inbox,
        ITenantSequence sequence, IMessageBus bus, ICacheInvalidator cache, IOptions<InvoicingOptions> options, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);

        string tenant;
        if (command.Source == InvoiceSource.Webhook)
        {
            tenant = order?.TenantId ?? caller.TenantId
                ?? throw new DomainException($"The tenant of order {command.OrderId} is not known yet.");
        }
        else
        {
            tenant = caller.TenantId ?? throw new NotFoundException($"Order {command.OrderId} was not found.");
            if (order is not null)
            {
                var callerScope = await scope.GetAsync(cancellationToken);
                if (order.TenantId != tenant || !callerScope.Allows(order.RequesterId, order.CostCenterCode))
                {
                    throw new NotFoundException($"Order {command.OrderId} was not found.");
                }
            }
        }

        var supplier = string.IsNullOrWhiteSpace(command.SupplierId) ? order?.SupplierId ?? "" : command.SupplierId.Trim();
        var number = command.SupplierInvoiceNumber?.Trim() ?? "";
        var lines = BuildLines(command.Lines, order);
        var stated = Money.Zar(command.StatedTotalInclVat);

        var existing = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(
                i => i.TenantId == tenant && i.SupplierId == supplier && i.SupplierInvoiceNumber == number, cancellationToken);
        var replay = command.WebhookEventId is not null
            && !await inbox.TryRecordAsync(WebhookSource, command.WebhookEventId, cancellationToken);
        if (existing is not null || replay)
        {
            if (existing is null)
            {
                throw new ConflictException("That webhook event was already processed.");
            }

            return existing.HasSameContentAs(command.OrderId, command.InvoiceDate, lines, stated)
                ? InvoicingMapper.ToDto(existing)
                : throw new ConcurrencyConflictException(
                    $"Invoice {number} of {supplier} is already recorded with other content.");
        }

        var now = time.GetUtcNow();
        var invoice = Invoice.Create(
            tenant, command.OrderId, order?.RequestId ?? Guid.Empty, supplier, number, command.InvoiceDate,
            await sequence.NextAsync(tenant, "invoice", cancellationToken), command.Source, lines, stated, command.Document, now);
        db.Invoices.Add(invoice);

        MatchOutcome? outcome = null;
        if (order is not null)
        {
            var earlier = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines)
                .Where(i => i.TenantId == tenant && i.OrderId == order.Id)
                .ToListAsync(cancellationToken);
            outcome = MatchDecisions.Rematch(order, [.. earlier, invoice], Money.Zar(options.Value.PriceTolerance), only: invoice).Single();
        }

        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(tenant, invoice.Id));
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Order {command.OrderId}", cancellationToken);
        if (outcome is not null)
        {
            await bus.PublishAsync(InvoicingMapper.ToReceivedEvent(invoice, now));
            await bus.PublishAsync(InvoicingMapper.ToOutcomeEvent(invoice, now));
        }

        return InvoicingMapper.ToDto(invoice);
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

/// <summary>Loading an invoice and its order for a caller: missing and out-of-scope are both "not found".</summary>
internal static class InvoiceAccess
{
    public static async Task<(Invoice Invoice, OrderView? Order)> LoadAllowedAsync(
        IInvoicingDbContext db, ICallerScope scope, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new NotFoundException($"Invoice {invoiceId} was not found.");
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == invoice.OrderId, cancellationToken);
        if (order is not null)
        {
            var callerScope = await scope.GetAsync(cancellationToken);
            if (!callerScope.Allows(order.RequesterId, order.CostCenterCode))
            {
                throw new NotFoundException($"Invoice {invoiceId} was not found.");
            }
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
        if (order is null)
        {
            throw new ConflictException("The order of this invoice is not known yet, so nothing can be paid.");
        }

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
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Invoice {command.InvoiceId}", cancellationToken);
        await bus.PublishAsync(InvoicingMapper.ToReleasedEvent(invoice, payment, now));
        if (settled)
        {
            await bus.PublishAsync(InvoicingMapper.ToSettledEvent(order, now));
        }

        await bus.SendAsync(new ExportPayment(payment.Id, 1));
        return InvoicingMapper.ToDto(invoice);
    }
}

public static class HoldInvoiceHandler
{
    /// <summary>Puts the invoice on hold (no payment until released). Same access and version rules as paying.</summary>
    public static async Task<InvoiceDto> Handle(
        HoldInvoice command, IInvoicingDbContext db, ICallerScope scope, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        var (invoice, _) = await InvoiceAccess.LoadAllowedAsync(db, scope, command.InvoiceId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Invoice {command.InvoiceId}", invoice.Version, command.ExpectedVersion);
        invoice.Hold(command.Reason);
        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Invoice {command.InvoiceId}", cancellationToken);
        return InvoicingMapper.ToDto(invoice);
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
        IOptions<InvoicingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var (invoice, order) = await InvoiceAccess.LoadAllowedAsync(db, scope, command.InvoiceId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Invoice {command.InvoiceId}", invoice.Version, command.ExpectedVersion);
        invoice.ReleaseHold();
        var now = time.GetUtcNow();
        MatchOutcome? outcome = null;
        if (order is not null && invoice.Status is InvoiceStatus.Matched or InvoiceStatus.Mismatched)
        {
            var all = await db.Invoices.Include(i => i.Lines).Where(i => i.OrderId == order.Id).ToListAsync(cancellationToken);
            outcome = MatchDecisions.Rematch(order, all, Money.Zar(options.Value.PriceTolerance), only: invoice).SingleOrDefault();
        }

        await cache.RemoveAfterCommitAsync(InvoicingCacheTag.Of(invoice.TenantId, invoice.Id));
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Invoice {command.InvoiceId}", cancellationToken);
        if (outcome is { Changed: true })
        {
            await bus.PublishAsync(InvoicingMapper.ToOutcomeEvent(invoice, now));
        }

        return InvoicingMapper.ToDto(invoice);
    }
}
