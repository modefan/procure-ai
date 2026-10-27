using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Application.Mapping;
using ProcureAi.Purchasing.Domain;
using Wolverine;

namespace ProcureAi.Purchasing.Application.Commands;

/// <summary>Cancels the order of a request, or leaves a tombstone when there is none yet (a cancel can overtake the create).</summary>
public static class CancelPurchaseOrderHandler
{
    /// <summary>
    /// No order: a tombstone is stored so a later create does nothing, and <c>PurchaseOrderCancelled</c> is published without an
    /// order ID. Cancelled: published, and when the supplier already has the order it is told (retried with backoff). Cancelled
    /// before: the same answer again. Too late (goods received, supplier rejected it): <c>PurchaseOrderCancellationRefused</c>.
    /// </summary>
    public static async Task Handle(
        CancelPurchaseOrder command, IPurchasingDbContext db, IMessageBus bus, ICacheInvalidator cache, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var order = await db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == command.TenantId && o.RequestId == command.RequestId, cancellationToken);
        if (order is null)
        {
            var tombstone = PurchaseOrder.CancelledBeforeCreation(command.TenantId, command.RequestId, command.Reason, now);
            db.Orders.Add(tombstone);
            await db.SaveChangesAsync(cancellationToken);
            await bus.PublishAsync(new PurchaseOrderCancelled(null, command.RequestId, command.TenantId, command.Reason, now));
            return;
        }

        var outcome = order.Cancel(command.Reason, now);
        await cache.RemoveAfterCommitAsync(PurchasingCacheTag.Of(order.TenantId, order.Id));
        await db.SaveChangesAsync(cancellationToken);
        var orderId = order.Number is null ? (Guid?)null : order.Id;
        switch (outcome)
        {
            case CancelOutcome.Cancelled cancelled:
                await bus.PublishAsync(new PurchaseOrderCancelled(orderId, order.RequestId, order.TenantId, command.Reason, now));
                if (cancelled.SupplierMustBeTold)
                {
                    await bus.SendAsync(new CancelOrderAtSupplier(order.Id, 1));
                }

                break;
            case CancelOutcome.AlreadyCancelled:
                await bus.PublishAsync(new PurchaseOrderCancelled(orderId, order.RequestId, order.TenantId, order.CancelReason ?? command.Reason, now));
                break;
            case CancelOutcome.Refused refused:
                await bus.PublishAsync(new PurchaseOrderCancellationRefused(order.Id, order.RequestId, order.TenantId, refused.Reason, now));
                break;
        }
    }
}

public sealed record ReceiptLineInput(int LineNo, int Quantity);

/// <summary>Books a delivery against an order. <c>ExpectedVersion</c> is the version the caller last saw.</summary>
public sealed record ReceiveGoods(Guid OrderId, uint ExpectedVersion, IReadOnlyList<ReceiptLineInput> Lines, string? DeliveryNote);

/// <summary>Accepts that the rest of an order will not arrive.</summary>
public sealed record CloseOrderShort(Guid OrderId, uint ExpectedVersion, string Reason);

public static class ReceiveGoodsHandler
{
    /// <summary>
    /// An order outside the caller's scope is not found (404, the same as a missing one). A stale version is a 412, checked before
    /// the domain rules. The receipt and <c>GoodsReceived</c> commit together (outbox). The permission <c>orders.receive</c> is
    /// required by the controller policy, and the controller evicts the cached reads (<see cref="PurchasingCacheTag.Of"/>) after the call, like Requests.
    /// </summary>
    public static async Task<PurchaseOrderDto> Handle(
        ReceiveGoods command, IPurchasingDbContext db, ICallerContext caller, ICallerScope scope, IMessageBus bus,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await OrderAccess.LoadAllowedAsync(db, scope, command.OrderId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Order {command.OrderId}", order.Version, command.ExpectedVersion);
        var receipt = order.Receive(
            [.. command.Lines.Select(l => (l.LineNo, l.Quantity))], caller.UserId, command.DeliveryNote, time.GetUtcNow());
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Order {command.OrderId}", cancellationToken);
        await bus.PublishAsync(PurchasingMapper.ToGoodsReceivedEvent(order, receipt));
        return PurchasingMapper.ToDto(order);
    }
}

public static class CloseOrderShortHandler
{
    /// <summary>Same access, version and outbox rules as receiving. Only a partly received order can be closed short.</summary>
    public static async Task<PurchaseOrderDto> Handle(
        CloseOrderShort command, IPurchasingDbContext db, ICallerContext caller, ICallerScope scope, IMessageBus bus,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await OrderAccess.LoadAllowedAsync(db, scope, command.OrderId, cancellationToken);
        OptimisticConcurrency.EnsureVersion($"Order {command.OrderId}", order.Version, command.ExpectedVersion);
        var now = time.GetUtcNow();
        var closed = order.CloseShort(command.Reason, caller.UserId, now);
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, $"Order {command.OrderId}", cancellationToken);
        await bus.PublishAsync(PurchasingMapper.ToClosedShortEvent(order, closed, command.Reason, caller.UserId, now));
        return PurchasingMapper.ToDto(order);
    }
}

/// <summary>Loading an order for a caller: tombstones, missing and out-of-scope orders are all "not found".</summary>
internal static class OrderAccess
{
    public static async Task<PurchaseOrder> LoadAllowedAsync(
        IPurchasingDbContext db, ICallerScope scope, Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Lines).Include(o => o.Receipts)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return await EnsureAllowedAsync(order, scope, id, cancellationToken);
    }

    public static async Task<PurchaseOrder> EnsureAllowedAsync(
        PurchaseOrder? order, ICallerScope scope, object what, CancellationToken cancellationToken)
    {
        if (order is null || order.Number is null)
        {
            throw new NotFoundException($"Order {what} was not found.");
        }

        var callerScope = await scope.GetAsync(cancellationToken);
        return callerScope.Allows(order.RequesterId, order.CostCenterCode)
            ? order
            : throw new NotFoundException($"Order {what} was not found.");
    }
}
