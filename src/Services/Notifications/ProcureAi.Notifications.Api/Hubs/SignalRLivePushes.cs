using Microsoft.AspNetCore.SignalR;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Live;
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
}
