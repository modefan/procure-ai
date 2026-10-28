using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Presence;

namespace ProcureAi.Notifications.Application.Live;

/// <summary>Sends live updates to the connected clients (the SignalR hub, in the Api; across instances through the Redis backplane).</summary>
public interface ILivePushes
{
    /// <summary>Sends each change, in order, to every connection of <paramref name="tenantId"/> (group <c>t:{tenantId}</c>).</summary>
    Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken);

    /// <summary><c>ViewersChanged(entity, id, viewers)</c> to every connection watching the entity (group <c>v:{tenantId}:{entity}:{id}</c>).</summary>
    Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken);

    /// <summary><c>OnlineChanged(users)</c> to every connection of the tenant (group <c>t:{tenantId}</c>).</summary>
    Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken);

    /// <summary><c>NotificationAdded(item)</c> to every connection of one user of the tenant (group <c>u:{tenantId}:{userId}</c>).</summary>
    Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken);
}
