using ProcureAi.Contracts.Budgets;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live updates for the events of the <c>budgets</c> exchange: each event becomes the pushes <see cref="LiveEventMap"/> names, sent to its tenant.
/// One method per event type: Wolverine 6.53 hides an interface handler behind any type-specific one, so nothing is left to it.
/// </summary>
public static class BudgetEventsHandler
{
    public static Task Handle(BudgetReserved message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(BudgetReservationFailed message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(BudgetReleased message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(BudgetSpent message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(BudgetAmountSet message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
}
