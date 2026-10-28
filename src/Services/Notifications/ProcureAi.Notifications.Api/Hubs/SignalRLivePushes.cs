using Microsoft.AspNetCore.SignalR;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Domain;

namespace ProcureAi.Notifications.Api.Hubs;

/// <summary>Sends live updates through the hub (to the clients of every instance, through the Redis backplane).</summary>
public sealed class SignalRLivePushes(IHubContext<LiveHub> hub) : ILivePushes
{
    public async Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        var group = hub.Clients.Group(LiveGroups.Tenant(tenantId));
        foreach (var change in changes)
        {
            await group.SendAsync(LiveHubContract.EntityChangedMethod, change, cancellationToken);
        }
    }

    public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken) =>
        hub.Clients.Group(LiveGroups.Viewers(tenantId, target.Entity, target.Id))
            .SendAsync(LiveHubContract.ViewersChangedMethod, target.Entity, target.Id, viewers, cancellationToken);

    public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken) =>
        hub.Clients.Group(LiveGroups.Tenant(tenantId)).SendAsync(LiveHubContract.OnlineChangedMethod, online, cancellationToken);

    public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken) =>
        hub.Clients.Group(LiveGroups.User(tenantId, userId)).SendAsync(LiveHubContract.NotificationAddedMethod, item, cancellationToken);
}
