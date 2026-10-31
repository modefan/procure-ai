using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>A tenant the user belongs to, as Access lists it (<c>internal/users/{userId}/tenants</c>).</summary>
public sealed record UserTenant(string Id, string Name);

/// <summary>Asks Access which tenants a user belongs to.</summary>
public interface IUserTenantsClient
{
    /// <summary>Every tenant of the user, with its name. Throws when Access cannot answer (the caller decides what that means).</summary>
    Task<IReadOnlyList<UserTenant>> ListAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// The gateway's call to <c>GET internal/users/{userId}/tenants</c> as its service client. Not cached: a switch must follow a
/// membership change at once, and the call is made once per page load (<c>/bff/user</c>) and per switch.
/// </summary>
public sealed class UserTenantsClient(HttpClient http) : IUserTenantsClient
{
    public async Task<IReadOnlyList<UserTenant>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/access/internal/users/{Uri.EscapeDataString(userId)}/tenants");
        request.Options.Set(ServiceTokenHandler.OnBehalfOfUserOption, userId);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<UserTenant>>(cancellationToken)
               ?? throw new InvalidOperationException("Access returned an empty list of tenants.");
    }
}

/// <summary>
/// The scope parameter that asks the identity provider for one organization (Keycloak 26: <c>organization:&lt;alias&gt;</c>). The
/// bare <c>organization</c> scope (any organization: Keycloak's picker) cannot be sent next to it (Keycloak answers
/// <c>invalid_scope</c>), so it is replaced.
/// </summary>
public static class OrganizationScope
{
    public const string Name = "organization";

    public static string For(IEnumerable<string> scopes, string tenant) => string.Join(' ',
        scopes.Where(s => s != Name && !s.StartsWith(Name + ":", StringComparison.Ordinal))
            .Append($"{Name}:{tenant}")
            .Distinct(StringComparer.Ordinal));
}

/// <summary>
/// Replaces the browser's session with a new one under a new key, and removes the old one from the ticket store and from the user's
/// index (so neither a copy of the old cookie nor "log out everywhere" ever finds it again).
/// </summary>
public static class SessionRotation
{
    /// <summary>Set in <see cref="HttpContext.Items"/>: the cookie handler must not see the request's session cookie (it stores a new session).</summary>
    public const string HideRequestCookieItem = "procure.session.hide-request-cookie";

    /// <summary>The claim in the session cookie that holds the ticket store key (ASP.NET Core's cookie handler).</summary>
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    /// <summary>The ticket store key of the request's session cookie, or null when it has none.</summary>
    public static string? CurrentKey(HttpContext http, CookieAuthenticationOptions cookie)
    {
        var value = cookie.CookieManager is SessionCookieManager manager
            ? manager.ReadRequestCookie(http, cookie.Cookie.Name!)
            : cookie.CookieManager.GetRequestCookie(http, cookie.Cookie.Name!);
        return string.IsNullOrEmpty(value) ? null : cookie.TicketDataFormat.Unprotect(value)?.Principal.FindFirst(SessionIdClaim)?.Value;
    }

    /// <summary>
    /// Within a request that already read the session (an endpoint behind the session policy): signs the old session out (the cookie
    /// handler removes it from the store and will not renew it at the end of the response), then signs in on a fresh cookie handler that
    /// does not see the old cookie, so the new ticket gets a new key.
    /// </summary>
    public static async Task ReplaceAsync(HttpContext http, ClaimsPrincipal principal, AuthenticationProperties properties)
    {
        await http.SignOutAsync(BffSchemes.Cookie);
        http.Items[HideRequestCookieItem] = true;
        var provider = new AuthenticationHandlerProvider(http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>());
        var handler = await provider.GetHandlerAsync(http, BffSchemes.Cookie) as IAuthenticationSignInHandler
                      ?? throw new InvalidOperationException("The session scheme cannot sign in.");
        await handler.SignInAsync(principal, properties);
    }
}

/// <summary>The session cookie as usual (chunked), except that a request marked by <see cref="SessionRotation"/> reads as having none.</summary>
public sealed class SessionCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public string? GetRequestCookie(HttpContext context, string key) =>
        context.Items.ContainsKey(SessionRotation.HideRequestCookieItem) ? null : _inner.GetRequestCookie(context, key);

    /// <summary>The request's cookie whatever the mark says.</summary>
    public string? ReadRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, key);

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, key, value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) => _inner.DeleteCookie(context, key, options);
}

/// <summary>
/// <c>GET /bff/switch-tenant?tenant=globex&amp;returnUrl=/app/dashboard</c> (plan 06 ruling 18): the signed-in user moves to another
/// company they belong to. The membership is checked with Access first (not a member, or an unknown ID: 403). A code-flow session then
/// gets a token for exactly that organization through a silent OIDC challenge (<c>prompt=none</c>, scope <c>organization:globex</c>);
/// a demo session through the demo client's password grant. Either way the callback checks that the token is for the same user and
/// that tenant, and the session is replaced by a new one (<see cref="SessionRotation"/>). The return URL is a local path only.
/// </summary>
public static class TenantSwitchEndpoints
{
    /// <summary>Items of the challenge's (protected) state, read back at the callback.</summary>
    public const string TenantItem = "procure.switch.tenant";
    public const string SubjectItem = "procure.switch.sub";
    public const string SessionItem = "procure.switch.session";
    public const string InteractiveItem = "procure.switch.interactive";

    /// <summary><see cref="HttpContext.Items"/> key: where a refused switch callback sends the browser back to.</summary>
    public const string RefusedReturnItem = "procure.switch.refused-return";

    public static IEndpointRouteBuilder MapTenantSwitch(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/switch-tenant", SwitchAsync).RequireAuthorization(BffAuthSetup.SessionPolicy);
        return app;
    }

    private static async Task<IResult> SwitchAsync(
        HttpContext http, string? tenant, string? returnUrl, ICurrentUser user, IUserTenantsClient tenants,
        IOptionsMonitor<CookieAuthenticationOptions> cookies, IOptions<DemoLoginOptions> demoLogin, DemoSignIn demoSignIn,
        TokenRefresher refresher, ILogger<UserTenantsClient> logger)
    {
        var target = BffEndpoints.SafeReturnUrl(returnUrl);
        var subject = http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(subject))
        {
            return Forbidden();
        }

        IReadOnlyList<UserTenant> memberships;
        try
        {
            memberships = await tenants.ListAsync(subject, http.RequestAborted);
        }
        // Anything but the browser's own abort: the Access client's resilience pipeline throws its own exceptions too (an open circuit,
        // the total timeout), and every one of them means "cannot check the membership now", never a 500.
        catch (Exception exception) when (!http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Tenant switch: Access could not list the user's tenants");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Switching company is not available right now");
        }

        // Exactly one of the IDs Access lists (ordinal): anything else, also an existing organization the user is not in, is a 403.
        if (!memberships.Any(m => string.Equals(m.Id, tenant, StringComparison.Ordinal)))
        {
            return Forbidden();
        }

        http.Response.Headers.CacheControl = "no-store";
        if (string.Equals(user.TenantId, tenant, StringComparison.Ordinal))
        {
            return Results.Redirect(target);
        }

        var session = (await http.AuthenticateAsync(BffSchemes.Cookie)).Properties;
        var demoClient = demoLogin.Value.ClientId;
        if (session is not null && session.Items.TryGetValue(TokenRefresher.ClientItem, out var client)
            && string.Equals(client, demoClient, StringComparison.Ordinal))
        {
            return await SwitchDemoSessionAsync(http, tenant, subject, target, session, demoLogin.Value, demoSignIn, refresher);
        }

        var properties = new AuthenticationProperties { RedirectUri = target };
        properties.Items[TenantItem] = tenant;
        properties.Items[SubjectItem] = subject;
        // The old session's store key travels in the state, which Data Protection encrypts and signs like the session cookie itself: the
        // callback usually arrives without the cookie (SameSite=Strict after the cross-site hop through the identity provider).
        if (SessionRotation.CurrentKey(http, cookies.Get(BffSchemes.Cookie)) is { } key)
        {
            properties.Items[SessionItem] = key;
        }

        return Results.Challenge(properties, [BffSchemes.Oidc]);
    }

    /// <summary>A one-click demo session has no single sign-on at the provider: the demo client signs the same demo user in again.</summary>
    private static async Task<IResult> SwitchDemoSessionAsync(
        HttpContext http, string tenant, string subject, string target, AuthenticationProperties session, DemoLoginOptions demo,
        DemoSignIn demoSignIn, TokenRefresher refresher)
    {
        var username = http.User.FindFirst("preferred_username")?.Value;
        var demoUser = demo.Enabled
            ? demo.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
            : null;
        if (demoUser is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Switching company is not available right now");
        }

        var signIn = await demoSignIn.AcquireAsync(http, demoUser, tenant);
        if (signIn is null || !string.Equals(signIn.Value.Principal.FindFirst("sub")?.Value, subject, StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Switching company is not available right now");
        }

        // The password grant opened a new session at the provider; the one it replaces ends there too (like a logout), so repeated
        // switches do not pile up provider sessions. A code-flow switch shares the browser's single sign-on session and keeps it.
        await refresher.EndProviderSessionAsync(session.GetTokenValue("refresh_token"), session, http.RequestAborted);
        await SessionRotation.ReplaceAsync(http, signIn.Value.Principal, signIn.Value.Properties);
        return Results.Redirect(target);
    }

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "You do not belong to that company");

    /// <summary>
    /// The callback of a switch (OIDC <c>OnTokenValidated</c>): the token must be for the user who asked, and for exactly the tenant asked
    /// for. Returns the reason to refuse it, or null.
    /// </summary>
    public static string? Refusal(AuthenticationProperties properties, ClaimsPrincipal principal)
    {
        if (!properties.Items.TryGetValue(TenantItem, out var tenant) || tenant is null)
        {
            return null;
        }

        if (!properties.Items.TryGetValue(SubjectItem, out var subject)
            || !string.Equals(principal.FindFirst("sub")?.Value, subject, StringComparison.Ordinal))
        {
            return "the token is for another user";
        }

        var tenants = principal.FindAll("tenant").Select(c => c.Value).ToArray();
        return tenants is [var only] && string.Equals(only, tenant, StringComparison.Ordinal) ? null : "the token is not for the tenant asked for";
    }

    /// <summary>The switch's items are for the callback only; the new session's ticket does not keep them.</summary>
    public static void Forget(AuthenticationProperties properties)
    {
        foreach (var item in new[] { TenantItem, SubjectItem, SessionItem, InteractiveItem, BffAuthSetup.PromptItem })
        {
            properties.Items.Remove(item);
        }
    }
}
