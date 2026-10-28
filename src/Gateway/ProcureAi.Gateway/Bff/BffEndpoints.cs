using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Gateway.Bff;

/// <summary>The <c>/bff</c> endpoints the Angular app talks to: sign in, register, who am I, sign out.</summary>
public static class BffEndpoints
{
    public static IEndpointRouteBuilder MapBff(this IEndpointRouteBuilder app)
    {
        var bff = app.MapGroup("/bff");

        bff.MapGet("/login", (string? returnUrl) => SignIn(returnUrl, prompt: null)).AllowAnonymous();

        // OIDC prompt=create opens the identity provider's registration form. A new user has no tenant or roles
        // yet; the first /bff/user call puts them in the demo company.
        bff.MapGet("/register", (string? returnUrl) => SignIn(returnUrl, prompt: "create")).AllowAnonymous();

        bff.MapGet("/user", GetUserAsync).RequireAuthorization(BffAuthSetup.SessionPolicy);
        bff.MapPost("/logout", LogoutAsync).RequireAuthorization(BffAuthSetup.SessionPolicy);
        bff.MapPost("/logout-all", LogoutAllAsync).RequireAuthorization(BffAuthSetup.SessionPolicy);

        // An unknown /bff path is a 404, not the web app's index page. Order: after the real endpoints.
        bff.Map("/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"))
            .AllowAnonymous().WithOrder(500);
        return app;
    }

    private static IResult SignIn(string? returnUrl, string? prompt)
    {
        var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
        if (prompt is not null)
        {
            // Read by BffAuthSetup when it builds the request to the identity provider.
            properties.Items[BffAuthSetup.PromptItem] = prompt;
        }

        return Results.Challenge(properties, [BffSchemes.Oidc]);
    }

    private static async Task<IResult> GetUserAsync(
        HttpContext http, ICurrentUser user, IPermissionProvider permissions, IAntiforgery antiforgery, DemoRegistration registration,
        CancellationToken cancellationToken)
    {
        // A user without a tenant just registered: they join the demo company (and get their new claims) on this call.
        // When that fails they are answered as before: no tenant, no permissions.
        // "No tenant" means "not in any company": that also covers a user taken out of their organization, who is joined to
        // demo as a requester with a R 0 limit again. Accepted for the demo.
        if (user.TenantId is null)
        {
            await registration.TryJoinAsync(http);
        }

        // A user without a tenant has no permissions, whatever roles the token carries.
        IReadOnlySet<string> granted = user.TenantId is null
            ? new HashSet<string>()
            : await permissions.GetPermissionsAsync(user, cancellationToken);

        // The app copies this cookie into the X-XSRF-TOKEN header of every state-changing call.
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(XsrfMiddleware.CookieName, tokens.RequestToken!, XsrfCookieOptions());

        var principal = http.User;
        return Results.Json(new
        {
            userId = user.UserId,
            name = principal.FindFirst("name")?.Value ?? principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst("email")?.Value,
            tenantId = user.TenantId,
            roles = user.Roles.Order(StringComparer.Ordinal),
            permissions = granted.Order(StringComparer.Ordinal),
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, TokenRefresher refresher)
    {
        ClearBrowserCache(http);
        await refresher.EndProviderSessionAsync(await http.GetTokenAsync(BffSchemes.Cookie, "refresh_token"), http.RequestAborted);
        await http.SignOutAsync(BffSchemes.Cookie);
        http.Response.Cookies.Delete(XsrfMiddleware.CookieName, XsrfCookieOptions());
        return Results.NoContent();
    }

    /// <summary>Revokes every session of this user, in every browser, then ends this one like a normal sign-out.</summary>
    private static async Task<IResult> LogoutAllAsync(HttpContext http, RedisTicketStore store, TokenRefresher refresher)
    {
        ClearBrowserCache(http);
        var refreshToken = await http.GetTokenAsync(BffSchemes.Cookie, "refresh_token");
        var subject = http.User.FindFirst("sub")?.Value;
        var revoked = subject is null ? 0 : await store.RevokeAllForUserAsync(subject, http.RequestAborted);
        await refresher.EndProviderSessionAsync(refreshToken, http.RequestAborted);
        await http.SignOutAsync(BffSchemes.Cookie);
        http.Response.Cookies.Delete(XsrfMiddleware.CookieName, XsrfCookieOptions());
        return Results.Json(new { revoked });
    }

    /// <summary>
    /// Tells the browser to drop its HTTP cache for this site: API responses are revalidated with ETags (conditional GETs), so
    /// the next person to sign in on this browser must not be able to read the previous user's cached responses.
    /// </summary>
    private static void ClearBrowserCache(HttpContext http) => http.Response.Headers["Clear-Site-Data"] = "\"cache\"";

    /// <summary>Only a path on this site: no other host, no protocol-relative <c>//host</c>, no control characters.</summary>
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/' || returnUrl.Any(char.IsControl))
        {
            return "/";
        }

        return returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\') ? "/" : returnUrl;
    }

    private static CookieOptions XsrfCookieOptions() => new()
    {
        // The one cookie script must read.
        HttpOnly = false,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
