using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Reads the current user lazily from the HTTP context (usable before authentication has run).</summary>
public sealed partial class CurrentUser(IHttpContextAccessor accessor, IOptions<AuthOptions> auth) : ICurrentUser
{
    public const string AnonymousUserId = "anonymous";

    private const int MaxHeaderLength = 100;

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

    /// <summary>
    /// True for a token issued to one of our own services (its <c>azp</c> equals <c>Auth:InternalClientId</c>), which may
    /// act on behalf of a user. The admin API key is never a service account.
    /// </summary>
    public bool IsServiceAccount =>
        !IsAdminApiKey
        && auth.Value.InternalClientId.Length > 0
        && Principal?.FindFirst("azp")?.Value == auth.Value.InternalClientId;

    public string? TenantId
    {
        get
        {
            if ((IsAdminApiKey || IsServiceAccount) && accessor.HttpContext is { } http)
            {
                return ReadTenantHeader(http.Request);
            }

            var claim = Principal?.FindFirst("tenant")?.Value;
            return claim is not null && SafeId().IsMatch(claim) ? claim : null;
        }
    }

    public IReadOnlySet<string> Roles =>
        Principal?.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? [];

    // The API key handler validated these headers into claims. A service account's token carries no such claims, so its
    // headers are read directly; a normal user's headers are never trusted.
    public string? CallingService => IsAdminApiKey
        ? Principal?.FindFirst("calling_service")?.Value
        : IsServiceAccount ? ReadHeader(ApiKeyDefaults.CallingServiceHeader) : null;

    public string? OnBehalfOfUser => IsAdminApiKey
        ? Principal?.FindFirst("on_behalf_of")?.Value
        : IsServiceAccount ? ReadHeader(ApiKeyDefaults.OnBehalfOfUserHeader) : null;

    private string? ReadHeader(string name)
    {
        var value = accessor.HttpContext?.Request.Headers[name].ToString().Trim();
        return value is { Length: > 0 and <= MaxHeaderLength } && SafeId().IsMatch(value) ? value : null;
    }

    /// <summary>The <c>X-Tenant-Id</c> header when it looks like a tenant ID; otherwise null.</summary>
    internal static string? ReadTenantHeader(HttpRequest request)
    {
        var value = request.Headers[ApiKeyDefaults.TenantHeader].ToString().Trim();
        return SafeId().IsMatch(value) ? value : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z")]
    private static partial Regex SafeId();
}
