using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Notifications.Api.Hubs;

/// <summary>
/// The live hub's authorization: an authenticated caller that has a tenant (<see cref="ICurrentUser.TenantId"/>: the token's
/// <c>tenant</c> claim; <c>X-Tenant-Id</c> only for the admin key or a service account). Anything else is refused at negotiate.
/// </summary>
public static class LiveHubPolicy
{
    public const string Name = "live-hub";

    public static AuthorizationBuilder AddLiveHubPolicy(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.Resource is HttpContext http
                && Caller(http, http.RequestServices.GetRequiredService<IOptions<AuthOptions>>()).TenantId is not null));

    /// <summary>The caller of a hub request, read from that request (a hub has no ambient HTTP context to rely on).</summary>
    public static ICurrentUser Caller(HttpContext? http, IOptions<AuthOptions> auth) =>
        new CurrentUser(new FixedHttpContextAccessor(http), auth);

    private sealed class FixedHttpContextAccessor(HttpContext? http) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => http;
            set => throw new NotSupportedException("The hub's caller is fixed to its own request.");
        }
    }
}
