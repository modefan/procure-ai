using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Presence;

/// <summary>
/// Presence (plan 04 ruling 11): who is online in a tenant and who views which entity. Every change is sent to the clients concerned
/// (<c>OnlineChanged</c>, <c>ViewersChanged</c>); the lists hold distinct users (a second tab counts once). State lives in
/// <see cref="IPresenceStore"/> (Redis), so a connection that stops sending heartbeats is dropped within the TTL by whichever instance
/// sweeps, also when its own instance is gone.
/// </summary>
public sealed class PresenceService(
    IPresenceStore store,
    ILivePushes pushes,
    IOptions<PresenceOptions> options,
    [FromKeyedServices(PresenceClock.Key)] TimeProvider clock,
    ILogger<PresenceService> logger)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    private TimeSpan Ttl => options.Value.Ttl;

    /// <summary>A new connection: online now; everyone of the tenant gets the new online list.</summary>
    public async Task ConnectedAsync(PresenceConnection connection, CancellationToken cancellationToken)
    {
        await store.UpsertAsync(connection, Now, cancellationToken);
        await PushOnlineAsync(connection.TenantId, cancellationToken);
    }

    /// <summary>
    /// Keeps the connection (and what it watches) alive for another TTL. A connection the sweeper had dropped comes back online and as a
    /// viewer of what it watched, and the lists are sent again.
    /// </summary>
    public async Task HeartbeatAsync(PresenceConnection connection, CancellationToken cancellationToken)
    {
        var upserted = await store.UpsertAsync(connection, Now, cancellationToken);
        if (!upserted.CameOnline)
        {
            return;
        }

        await PushOnlineAsync(connection.TenantId, cancellationToken);
        foreach (var target in upserted.Targets)
        {
            await PushViewersAsync(connection.TenantId, target, cancellationToken);
        }
    }

    /// <summary>
    /// The connection views <paramref name="target"/>: everyone watching it (the caller too) gets the viewers. Returns them, or null when
    /// the connection already watches <see cref="PresenceOptions.MaxWatchesPerConnection"/> entities.
    /// </summary>
    public async Task<IReadOnlyList<Viewer>?> WatchAsync(
        PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken)
    {
        if (!await store.WatchAsync(connection, target, Now, options.Value.MaxWatchesPerConnection, cancellationToken))
        {
            return null;
        }

        var viewers = await ViewersOfAsync(connection.TenantId, target, cancellationToken);
        await pushes.PushViewersAsync(connection.TenantId, target, viewers, cancellationToken);
        return viewers;
    }

    /// <summary>The connection stops viewing <paramref name="target"/>: the remaining viewers get the new list.</summary>
    public async Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken)
    {
        await store.UnwatchAsync(connection, target, cancellationToken);
        await PushViewersAsync(connection.TenantId, target, cancellationToken);
    }

    /// <summary>A clean disconnect: the connection is no longer online or viewing anything.</summary>
    public async Task DisconnectedAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (await store.RemoveAsync(connectionId, cancellationToken) is { } removed)
        {
            await PushRemovedAsync([removed], cancellationToken);
        }
    }

    /// <summary>
    /// Drops every connection, of any instance, without a heartbeat for longer than the TTL, and sends the changed lists. Run by
    /// one instance at a time (the sweeper's lock). Each removal re-checks the heartbeat atomically, so a connection whose heartbeat
    /// arrived after the stale read stays. Returns how many were dropped.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = PresenceView.Cutoff(Now, Ttl);
        var stale = await store.StaleAsync(cutoff, cancellationToken);
        var removed = new List<RemovedConnection>();
        foreach (var connection in stale)
        {
            if (await store.RemoveIfStaleAsync(connection, cutoff, cancellationToken) is { } gone)
            {
                removed.Add(gone);
            }
        }

        if (removed.Count > 0)
        {
            logger.LogInformation("Presence sweep dropped {Count} connection(s) without a heartbeat", removed.Count);
            await PushRemovedAsync(removed, cancellationToken);
        }

        return removed.Count;
    }

    /// <summary>The users of the tenant online now.</summary>
    public async Task<IReadOnlyList<Viewer>> OnlineAsync(string tenantId, CancellationToken cancellationToken) =>
        PresenceView.Viewers(await store.OnlineAsync(tenantId, cancellationToken), Now, Ttl);

    private async Task<IReadOnlyList<Viewer>> ViewersOfAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken) =>
        PresenceView.Viewers(await store.ViewersAsync(tenantId, target, cancellationToken), Now, Ttl);

    private async Task PushOnlineAsync(string tenantId, CancellationToken cancellationToken) =>
        await pushes.PushOnlineAsync(tenantId, await OnlineAsync(tenantId, cancellationToken), cancellationToken);

    private async Task PushViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken) =>
        await pushes.PushViewersAsync(tenantId, target, await ViewersOfAsync(tenantId, target, cancellationToken), cancellationToken);

    /// <summary>One online list per tenant and one viewer list per entity, however many connections went.</summary>
    private async Task PushRemovedAsync(IReadOnlyList<RemovedConnection> removed, CancellationToken cancellationToken)
    {
        foreach (var tenant in removed.GroupBy(r => r.TenantId, StringComparer.Ordinal))
        {
            await PushOnlineAsync(tenant.Key, cancellationToken);
            foreach (var target in tenant.SelectMany(r => r.Targets).Distinct())
            {
                await PushViewersAsync(tenant.Key, target, cancellationToken);
            }
        }
    }
}
