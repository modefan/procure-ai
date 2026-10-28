using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Notifications.Api.Hubs;

/// <summary>
/// The live hub's authorization: an authenticated caller with a user ID (a non-empty <c>sub</c>) and a tenant
/// (<see cref="ICurrentUser.TenantId"/>: the token's <c>tenant</c> claim; <c>X-Tenant-Id</c> only for the admin key or a service account).
/// Anything else is refused at negotiate (403). The policy is evaluated with an <see cref="HttpContext"/> (negotiate, connect) and, for
/// hub methods that carry their own <c>[Authorize]</c>, with a <see cref="HubInvocationContext"/>: both read the caller from the
/// connection's request.
/// </summary>
public static class LiveHubPolicy
{
    public const string Name = "live-hub";

    public static AuthorizationBuilder AddLiveHubPolicy(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => IsTenantUser(context.Resource)));

    /// <summary>The caller of a hub request, read from that request (a hub has no ambient HTTP context to rely on).</summary>
    public static ICurrentUser Caller(HttpContext? http, IOptions<AuthOptions> auth) =>
        new CurrentUser(new FixedHttpContextAccessor(http), auth);

    /// <summary>
    /// True when the caller has a user ID and a tenant. A missing <c>sub</c> would make the user <c>anonymous</c> and put every such
    /// connection of the tenant into one shared user group.
    /// </summary>
    public static bool HasUserAndTenant(ICurrentUser caller) =>
        caller.IsAuthenticated && caller.UserId is { Length: > 0 } userId && userId != CurrentUser.AnonymousUserId
        && caller.TenantId is not null;

    private static bool IsTenantUser(object? resource)
    {
        var http = resource switch
        {
            HttpContext context => context,
            HubInvocationContext invocation => invocation.Context.GetHttpContext(),
            _ => null,
        };
        return http is not null
               && HasUserAndTenant(Caller(http, http.RequestServices.GetRequiredService<IOptions<AuthOptions>>()));
    }

    private sealed class FixedHttpContextAccessor(HttpContext? http) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => http;
            set => throw new NotSupportedException("The hub's caller is fixed to its own request.");
        }
    }
}
