using ProcureAi.Contracts.Notifications;

namespace ProcureAi.Notifications.Application.Live;

/// <summary>Sends live updates to the connected clients (the SignalR hub, in the Api; across instances through the Redis backplane).</summary>
public interface ILivePushes
{
    /// <summary>Sends each change, in order, to every connection of <paramref name="tenantId"/> (group <c>t:{tenantId}</c>).</summary>
    Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken);
}
