using ProcureAi.Contracts.Purchasing;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live updates for the events of the <c>purchasing</c> exchange: each event becomes the pushes <see cref="LiveEventMap"/> names, sent to its tenant.
/// One method per event type: Wolverine 6.53 hides an interface handler behind any type-specific one, so nothing is left to it.
/// </summary>
public static class PurchasingEventsHandler
{
    public static Task Handle(PurchaseOrderCreated message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderConfirmed message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderRejected message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(GoodsReceived message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderClosedShort message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderCancelled message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderCancellationRefused message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
}
