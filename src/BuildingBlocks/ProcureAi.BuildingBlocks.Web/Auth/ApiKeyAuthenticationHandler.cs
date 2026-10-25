using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// Authenticates the single admin API key (Claude's testing access) as <c>claude-admin</c>.
/// A wrong key always fails: it is never anonymous and never falls back to JWT. The key value is never logged.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyValidator validator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const int MaxHeaderLength = 100;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.KeyHeader, out var presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!validator.IsValid(presented.ToString()))
        {
            Logger.LogWarning("Rejected admin API key call to {Path}", Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new("sub", ApiKeyDefaults.AdminUserId),
            new(ApiKeyDefaults.AuthMethodClaim, ApiKeyDefaults.AuthMethodApiKey),
        };
        var callingService = Header(ApiKeyDefaults.CallingServiceHeader);
        var onBehalfOf = Header(ApiKeyDefaults.OnBehalfOfUserHeader);
        if (callingService is not null)
        {
            claims.Add(new Claim("calling_service", callingService));
        }

        if (onBehalfOf is not null)
        {
            claims.Add(new Claim("on_behalf_of", onBehalfOf));
        }

        // The user is not on the HttpContext yet, so the audit event carries its identity explicitly.
        Logger.LogInformation(
            "Admin API key call as {UserId} for tenant {TenantId} from {CallingService} on behalf of {OnBehalfOfUser}",
            ApiKeyDefaults.AdminUserId, CurrentUser.ReadTenantHeader(Request), callingService, onBehalfOf);

        var identity = new ClaimsIdentity(claims, Scheme.Name, "sub", "roles");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    private string? Header(string name)
    {
        var value = Request.Headers[name].ToString().Trim();
        return value.Length is > 0 and <= MaxHeaderLength ? value : null;
    }
}
