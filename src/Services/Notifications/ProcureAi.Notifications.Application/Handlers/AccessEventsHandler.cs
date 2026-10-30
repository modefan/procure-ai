using ProcureAi.Contracts.Access;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live update for <c>AccessChanged</c> (arrives on the service's <c>-access-changed</c> queue, not the live queue): a changed matrix
/// becomes an <c>access-matrix</c> push, so an open admin screen can reload. It runs in the same chain as the shared
/// <c>AccessChangedHandler</c> (cache eviction), as in Audit: two handler classes for one message type are both called, and this
/// one never takes the eviction's place. Other kinds push nothing.
/// </summary>
public static class AccessEventsHandler
{
    public static async Task Handle(AccessChanged message, ILivePushes pushes, CancellationToken cancellationToken)
    {
        var changes = LiveEventMap.For(message);
        if (changes.Count > 0)
        {
            await pushes.PushToTenantAsync(message.TenantId, changes, cancellationToken);
        }
    }
}
