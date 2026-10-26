using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Cookie events of the session: APIs answer 401/403 instead of redirecting to a login page, and the access token
/// is refreshed on the server shortly before it expires. When the refresh fails the session ends: the next call is a
/// 401 and the browser signs in again.
/// </summary>
public sealed class SessionCookieEvents(
    TokenRefresher refresher, IOptions<BffSessionOptions> session, TimeProvider time) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var expiresAt = TokenRefresher.ParseExpiresAt(context.Properties.GetTokenValue("expires_at"));
        if (expiresAt is null || expiresAt.Value - TimeSpan.FromSeconds(session.Value.RefreshSkewSeconds) > time.GetUtcNow())
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        var tokens = string.IsNullOrEmpty(refreshToken)
            ? null
            : await refresher.RefreshAsync(refreshToken, context.HttpContext.RequestAborted);
        if (tokens is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(BffSchemes.Cookie);
            return;
        }

        DemoRegistration.ApplyTokens(context.Properties, tokens, time.GetUtcNow());
        // Tenant and roles may have changed at the provider since sign-in.
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            AccessTokenClaims.Apply(identity, tokens.AccessToken);
            context.ReplacePrincipal(context.Principal);
        }

        // Stores the updated ticket in Redis.
        context.ShouldRenew = true;
    }
}
