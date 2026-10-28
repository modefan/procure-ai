using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Domain;

namespace ProcureAi.Notifications.Api.Hubs;

/// <summary>
/// Live updates (<c>/hubs/live</c>, reached through the gateway's <c>/hubs/{**}</c> route over WebSockets with the session's token).
/// Only a signed-in caller with a user ID and a tenant connects (<see cref="LiveHubPolicy"/>): the token's tenant, or <c>X-Tenant-Id</c> with the admin
/// key. The connection joins <c>t:{tenant}</c> and <c>u:{tenant}:{user}</c>; the client has no method to name another tenant or user. Server to
/// client: <c>EntityChanged</c> (<see cref="Contracts.Notifications.EntityChanged"/>), IDs only (plan 04 ruling 2); <c>OnlineChanged</c>,
/// <c>ViewersChanged</c> (presence, ruling 11) and <c>NotificationAdded</c> (the bell). Client to server: <c>Heartbeat()</c>,
/// <c>Watch(entity, id)</c> and <c>Unwatch(entity, id)</c>; the tenant of a watch is always the caller's own.
/// </summary>
[Authorize(Policy = LiveHubPolicy.Name)]
public sealed class LiveHub(IOptions<AuthOptions> auth, PresenceService presence, ILogger<LiveHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Checked again here (the policy ran on the negotiate and connect requests): a connection without a user ID or a tenant joins
        // nothing (no shared "anonymous" user group).
        if (Me() is not { } me)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Tenant(me.TenantId), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.User(me.TenantId, me.UserId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
        try
        {
            // After the joins, so this connection gets the online list too.
            await presence.ConnectedAsync(me, Context.ConnectionAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Presence is not worth the connection: live updates and the bell still work, and the next heartbeat brings it online.
            logger.LogWarning(exception, "Presence could not record connection {ConnectionId}; its next heartbeat will", Context.ConnectionId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // The connection is gone: nothing to cancel with. If this fails (Redis away), the sweeper drops it within the TTL.
        await presence.DisconnectedAsync(Context.ConnectionId, CancellationToken.None);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Keeps this connection online, and viewing what it watches, for another <c>Notifications:Presence:Ttl</c>.</summary>
    [Authorize(Policy = LiveHubPolicy.Name)]
    public Task Heartbeat() => presence.HeartbeatAsync(Required(), Context.ConnectionAborted);

    /// <summary>
    /// This connection views <paramref name="entity"/> <paramref name="id"/> of the caller's tenant: it joins the entity's viewer group and
    /// everyone there gets <c>ViewersChanged</c>. Returns the current viewers. An unknown entity, an ID that is not a plain identifier, or
    /// more than <c>MaxWatchesPerConnection</c> watched entities is refused with a hub error.
    /// </summary>
    [Authorize(Policy = LiveHubPolicy.Name)]
    public async Task<IReadOnlyList<Viewer>> Watch(string entity, string id)
    {
        var me = Required();
        var target = Target(entity, id);
        var group = LiveGroups.Viewers(me.TenantId, target.Entity, target.Id);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        IReadOnlyList<Viewer>? viewers;
        try
        {
            viewers = await presence.WatchAsync(me, target, Context.ConnectionAborted);
        }
        catch
        {
            // Not watching after all: no viewer pushes for this connection.
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, CancellationToken.None);
            throw;
        }

        if (viewers is null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            throw new HubException("This connection watches too many entities; unwatch one first.");
        }

        return viewers;
    }

    /// <summary>This connection no longer views the entity: it leaves the viewer group and the other viewers get <c>ViewersChanged</c>.</summary>
    [Authorize(Policy = LiveHubPolicy.Name)]
    public async Task Unwatch(string entity, string id)
    {
        var me = Required();
        var target = Target(entity, id);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroups.Viewers(me.TenantId, target.Entity, target.Id), Context.ConnectionAborted);
        await presence.UnwatchAsync(me, target, Context.ConnectionAborted);
    }

    /// <summary>
    /// The caller of this connection, from its request (token or admin key with <c>X-Tenant-Id</c>), never from method arguments. The name
    /// shown to others is the token's <c>name</c> claim, else the user ID: no Access call per heartbeat.
    /// </summary>
    private PresenceConnection? Me()
    {
        var caller = LiveHubPolicy.Caller(Context.GetHttpContext(), auth);
        if (!LiveHubPolicy.HasUserAndTenant(caller) || caller.TenantId is not { } tenantId)
        {
            return null;
        }

        var name = Context.User?.FindFirst("name")?.Value;
        return new PresenceConnection(Context.ConnectionId, tenantId, caller.UserId, string.IsNullOrWhiteSpace(name) ? caller.UserId : name.Trim());
    }

    private PresenceConnection Required() => Me() ?? throw new HubException("The connection has no user or tenant.");

    private static PresenceTarget Target(string entity, string id) =>
        PresenceTarget.TryCreate(entity, id, out var target)
            ? target
            : throw new HubException("Unknown entity, or an ID that is not 1 to 64 letters, digits, '.', '_' or '-'.");
}
