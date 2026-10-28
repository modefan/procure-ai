using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.Notifications.Domain;

namespace ProcureAi.Notifications.Api.Hubs;

/// <summary>
/// Live updates (<c>/hubs/live</c>, reached through the gateway's <c>/hubs/{**}</c> route over WebSockets with the session's token).
/// Only a signed-in caller with a tenant connects (<see cref="LiveHubPolicy"/>): the token's tenant, or <c>X-Tenant-Id</c> with the admin
/// key. The connection joins <c>t:{tenant}</c> and <c>u:{tenant}:{user}</c>; the client has no method to name another group. Server to
/// client: <c>EntityChanged</c> (<see cref="Contracts.Notifications.EntityChanged"/>), IDs only (plan 04 ruling 2).
/// </summary>
[Authorize(Policy = LiveHubPolicy.Name)]
public sealed class LiveHub(IOptions<AuthOptions> auth) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Checked again here (the policy ran on the negotiate and connect requests): a connection without a tenant joins nothing.
        var caller = LiveHubPolicy.Caller(Context.GetHttpContext(), auth);
        if (caller.TenantId is not { } tenantId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Tenant(tenantId), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.User(tenantId, caller.UserId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
