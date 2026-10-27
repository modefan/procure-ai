using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Application.Mapping;
using ProcureAi.Purchasing.Domain;
using Wolverine;

namespace ProcureAi.Purchasing.Application.Commands;

/// <summary>Local command: hand the order to the supplier. <c>Attempt</c> counts from 1.</summary>
public sealed record SendOrderToSupplier(Guid OrderId, int Attempt);

/// <summary>Local command: tell the supplier the order is cancelled. <c>Attempt</c> counts from 1.</summary>
public sealed record CancelOrderAtSupplier(Guid OrderId, int Attempt);

/// <summary>Creates the order of an approved request and starts sending it.</summary>
public static class CreatePurchaseOrderHandler
{
    /// <summary>
    /// An order (or tombstone) already exists for the request: nothing happens, whether the command is a duplicate or a cancel
    /// came first. Otherwise the order gets the next number, <c>PurchaseOrderCreated</c> is published and the sending is queued,
    /// all in one transaction. Two copies of the command racing each other collide on the unique (tenant, request) key and the loser
    /// is retried, then finds the order.
    /// </summary>
    public static async Task Handle(
        CreatePurchaseOrder command, IPurchasingDbContext db, ITenantSequence sequence, IMessageBus bus, ICacheInvalidator cache,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var exists = await db.Orders.IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == command.TenantId && o.RequestId == command.RequestId, cancellationToken);
        if (exists)
        {
            return;
        }

        var number = OrderNumber.FromSequence(await sequence.NextAsync(command.TenantId, OrderNumber.SequenceName, cancellationToken));
        var order = PurchaseOrder.Create(
            command.TenantId, command.RequestId, number, command.RequestNumber, command.RequesterId, command.CostCenterCode,
            command.SupplierId,
            [.. command.Lines.Select(l => new OrderLine(l.LineNo, l.Sku, l.Description, l.Quantity, Money.Zar(l.UnitPriceExclVat)))],
            time.GetUtcNow());
        db.Orders.Add(order);
        await cache.RemoveAfterCommitAsync(PurchasingCacheTag.Of(order.TenantId, order.Id));
        await db.SaveChangesAsync(cancellationToken);

        foreach (var created in order.DomainEvents.OfType<PurchaseOrderCreatedDomainEvent>())
        {
            await bus.PublishAsync(PurchasingMapper.ToCreatedEvent(created));
        }

        order.ClearDomainEvents();
        await bus.SendAsync(new SendOrderToSupplier(order.Id, 1));
    }
}

/// <summary>Sends an order to the supplier through the gateway.</summary>
public static class SendOrderToSupplierHandler
{
    /// <summary>
    /// Only an order still waiting for the supplier is sent (anything else: stop, so a repeat or a late retry does nothing).
    /// Accepted: the order is marked sent. Supplier unavailable: the failure is stored and the next attempt scheduled with
    /// backoff (<c>Purchasing:SupplierDelivery</c>), so the order stays visible as retrying and can still be cancelled. Supplier
    /// refuses it (400): the order is rejected and <c>PurchaseOrderRejected</c> published. If the order was cancelled or
    /// confirmed while the call was in flight, saving the "sent" mark loses on the version; the order is then read again, and
    /// when it was cancelled the supplier's reference is kept and the supplier is told to cancel.
    /// </summary>
    public static async Task Handle(
        SendOrderToSupplier command, IPurchasingDbContext db, ISupplierGateway gateway, IMessageBus bus, ICacheInvalidator cache,
        IOptions<PurchasingOptions> options, IPurchasingMetrics metrics, TimeProvider time, ILogger<SendOrderToSupplier> logger,
        CancellationToken cancellationToken)
    {
        var order = await Load(db, command.OrderId, cancellationToken);
        if (order is not { Status: OrderStatus.PendingSupplier })
        {
            return;
        }

        var supplierOrder = new SupplierOrder(order.Id, order.TenantId, PurchasingMapper.NumberText(order), [.. order.Lines]);
        var now = time.GetUtcNow();
        await cache.RemoveAfterCommitAsync(PurchasingCacheTag.Of(order.TenantId, order.Id));
        SupplierAcceptance acceptance;
        try
        {
            acceptance = await gateway.SubmitOrderAsync(supplierOrder, cancellationToken);
        }
        catch (SupplierUnavailableException exception)
        {
            metrics.SupplierSendFailed(exception.Reason);
            var delay = options.Value.SupplierDelivery.DelayFor(command.Attempt, Random.Shared);
            order.RecordSupplierFailure(exception.Message, now + delay);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(exception, "Supplier unavailable for order {OrderId}, attempt {Attempt}; retrying in {Delay}", order.Id, command.Attempt, delay);
            await bus.ScheduleAsync(new SendOrderToSupplier(order.Id, command.Attempt + 1), delay);
            return;
        }
        catch (SupplierRejectedException exception)
        {
            metrics.SupplierSendFailed("rejected");
            order.RejectBySupplier(exception.Message, now);
            await db.SaveChangesAsync(cancellationToken);
            await bus.PublishAsync(PurchasingMapper.ToRejectedEvent(order, exception.Message, now));
            return;
        }

        order.MarkSent(acceptance.SupplierReference, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await Load(db, command.OrderId, cancellationToken);
            if (current is { Status: OrderStatus.Cancelled })
            {
                current.RecordAcceptedAfterCancel(acceptance.SupplierReference);
                await db.SaveChangesAsync(cancellationToken);
                await bus.SendAsync(new CancelOrderAtSupplier(current.Id, 1));
            }
            else if (current is not { Status: OrderStatus.SentToSupplier or OrderStatus.Confirmed or OrderStatus.PartiallyReceived or OrderStatus.Received })
            {
                throw;
            }
        }
    }

    private static Task<PurchaseOrder?> Load(IPurchasingDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Orders.IgnoreQueryFilters().Include(o => o.Lines).Include(o => o.Receipts).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
}

/// <summary>Tells the supplier an order is cancelled.</summary>
public static class CancelOrderAtSupplierHandler
{
    /// <summary>
    /// For a cancelled order whose supplier has not been told yet. Unavailable supplier: the same backoff schedule as sending.
    /// A supplier that refuses the cancel for good (for example it already shipped) is logged and the order stops waiting: the
    /// demo has nothing more to try.
    /// </summary>
    public static async Task Handle(
        CancelOrderAtSupplier command, IPurchasingDbContext db, ISupplierGateway gateway, IMessageBus bus, ICacheInvalidator cache,
        IOptions<PurchasingOptions> options, ILogger<CancelOrderAtSupplier> logger, CancellationToken cancellationToken)
    {
        var order = await db.Orders.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is not { Status: OrderStatus.Cancelled, SupplierCancelPending: true, SupplierReference: { } reference })
        {
            return;
        }

        try
        {
            await gateway.CancelOrderAsync(reference, cancellationToken);
        }
        catch (SupplierUnavailableException exception)
        {
            var delay = options.Value.SupplierDelivery.DelayFor(command.Attempt, Random.Shared);
            logger.LogWarning(exception, "Supplier unavailable to cancel order {OrderId}, attempt {Attempt}; retrying in {Delay}", order.Id, command.Attempt, delay);
            await bus.ScheduleAsync(new CancelOrderAtSupplier(order.Id, command.Attempt + 1), delay);
            return;
        }
        catch (SupplierRejectedException exception)
        {
            logger.LogWarning(exception, "Supplier refused to cancel order {OrderId}; giving up", order.Id);
        }

        order.MarkSupplierCancelled();
        await cache.RemoveAfterCommitAsync(PurchasingCacheTag.Of(order.TenantId, order.Id));
        await db.SaveChangesAsync(cancellationToken);
    }
}

public enum SupplierOrderEventKind
{
    Confirmed,
    Declined,
}

/// <summary>An order event from the supplier webhook (already checked for signature and age by the Api).</summary>
public sealed record ApplySupplierOrderEvent(
    string EventId, SupplierOrderEventKind Kind, Guid OrderId, string SupplierReference, DateOnly? ExpectedDelivery, string? Reason);

public static class ApplySupplierOrderEventHandler
{
    public const string WebhookSource = "supplier";

    /// <summary>
    /// The event ID is recorded first, in the same unit of work as the effect: a replay changes nothing. An order that does not
    /// exist is logged and dropped (the webhook is answered 200). Confirm and decline follow the domain rules; an event that
    /// does not fit the order (for example a decline after the order was confirmed) is logged and dropped rather than retried.
    /// </summary>
    public static async Task Handle(
        ApplySupplierOrderEvent command, IPurchasingDbContext db, IWebhookInbox inbox, IMessageBus bus, ICacheInvalidator cache,
        TimeProvider time, ILogger<ApplySupplierOrderEvent> logger, CancellationToken cancellationToken)
    {
        if (!await inbox.TryRecordAsync(WebhookSource, command.EventId, cancellationToken))
        {
            return;
        }

        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Lines).Include(o => o.Receipts)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Supplier event {EventId} is for unknown order {OrderId}; ignored", command.EventId, command.OrderId);
            return;
        }

        var now = time.GetUtcNow();
        var decision = SupplierEventDecisions.Apply(order, command, now);
        if (decision.Ignored is { } ignored)
        {
            logger.LogWarning("Supplier event {EventId} ({Kind}) does not fit order {OrderId}: {Reason}", command.EventId, command.Kind, order.Id, ignored);
            return;
        }

        if (decision.Event is not null)
        {
            await bus.PublishAsync(decision.Event);
        }

        if (decision.CancelAtSupplier)
        {
            await bus.SendAsync(new CancelOrderAtSupplier(order.Id, 1));
        }

        await cache.RemoveAfterCommitAsync(PurchasingCacheTag.Of(order.TenantId, order.Id));
    }
}

/// <summary>What a supplier event does to an order: an event to publish, a cancel to send to the supplier, or why it was ignored.</summary>
public sealed record SupplierEventDecision(object? Event, bool CancelAtSupplier, string? Ignored);

/// <summary>The decisions of <see cref="ApplySupplierOrderEventHandler"/> as a pure function over the order, so they can be tested without infrastructure.</summary>
public static class SupplierEventDecisions
{
    /// <summary>
    /// Changes the order and says what follows. A confirmation for a cancelled order that never learned the supplier reference
    /// (the submit timed out after it reached the supplier) records the reference and asks for a cancel at the supplier; a second
    /// one, or a decline for a cancelled order, does nothing. An event that breaks a domain rule is reported as ignored.
    /// </summary>
    public static SupplierEventDecision Apply(PurchaseOrder order, ApplySupplierOrderEvent command, DateTimeOffset now)
    {
        try
        {
            switch (command.Kind)
            {
                case SupplierOrderEventKind.Confirmed when order.Status == OrderStatus.Cancelled:
                    return new SupplierEventDecision(null, order.RecordConfirmationAfterCancel(command.SupplierReference), null);
                case SupplierOrderEventKind.Confirmed when order.Confirm(command.SupplierReference, command.ExpectedDelivery, now):
                    return new SupplierEventDecision(PurchasingMapper.ToConfirmedEvent(order, now), false, null);
                case SupplierOrderEventKind.Declined:
                    var reason = command.Reason ?? "Declined by the supplier";
                    return order.RejectBySupplier(reason, now)
                        ? new SupplierEventDecision(PurchasingMapper.ToRejectedEvent(order, reason, now), false, null)
                        : new SupplierEventDecision(null, false, null);
                default:
                    return new SupplierEventDecision(null, false, null);
            }
        }
        catch (DomainException exception)
        {
            return new SupplierEventDecision(null, false, exception.Message);
        }
    }
}
