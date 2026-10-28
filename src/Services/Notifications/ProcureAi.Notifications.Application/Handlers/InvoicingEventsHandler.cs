using ProcureAi.Contracts.Invoicing;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live updates for the events of the <c>invoicing</c> exchange: each event becomes the pushes <see cref="LiveEventMap"/> names, sent to its tenant.
/// One method per event type: Wolverine 6.53 hides an interface handler behind any type-specific one, so nothing is left to it.
/// </summary>
public static class InvoicingEventsHandler
{
    public static Task Handle(InvoiceReceived message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(InvoiceMatched message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(InvoiceMismatched message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PaymentReleased message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PaymentExported message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PurchaseOrderSettled message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
}
