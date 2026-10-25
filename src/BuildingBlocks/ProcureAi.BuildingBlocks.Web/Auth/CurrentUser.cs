using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Reads the current user lazily from the HTTP context (usable before authentication has run).</summary>
public sealed partial class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string AnonymousUserId = "anonymous";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string UserId => Principal?.FindFirst("sub")?.Value ?? AnonymousUserId;

    /// <summary>
    /// True only for the identity our API key handler created. The authentication type is set by the handler and
    /// cannot be forged by a token, unlike the claim, which a JWT could also carry.
    /// </summary>
    public bool IsAdminApiKey =>
        Principal?.Identity?.AuthenticationType == ApiKeyDefaults.Scheme
        && Principal.FindFirst(ApiKeyDefaults.AuthMethodClaim)?.Value == ApiKeyDefaults.AuthMethodApiKey;

    public string? TenantId
    {
        get
        {
            if (IsAdminApiKey && accessor.HttpContext is { } http)
            {
                return ReadTenantHeader(http.Request);
            }

            var claim = Principal?.FindFirst("tenant")?.Value;
            return claim is not null && SafeId().IsMatch(claim) ? claim : null;
        }
    }

    public IReadOnlySet<string> Roles =>
        Principal?.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? [];

    public string? CallingService => IsAdminApiKey ? Principal?.FindFirst("calling_service")?.Value : null;

    public string? OnBehalfOfUser => IsAdminApiKey ? Principal?.FindFirst("on_behalf_of")?.Value : null;

    /// <summary>The <c>X-Tenant-Id</c> header when it looks like a tenant ID; otherwise null.</summary>
    internal static string? ReadTenantHeader(HttpRequest request)
    {
        var value = request.Headers[ApiKeyDefaults.TenantHeader].ToString().Trim();
        return SafeId().IsMatch(value) ? value : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z")]
    private static partial Regex SafeId();
}
