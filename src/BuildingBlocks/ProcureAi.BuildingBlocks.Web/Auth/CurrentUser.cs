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

    public bool IsAdminApiKey =>
        Principal?.FindFirst(ApiKeyDefaults.AuthMethodClaim)?.Value == ApiKeyDefaults.AuthMethodApiKey;

    public string? TenantId => IsAdminApiKey && accessor.HttpContext is { } http
        ? ReadTenantHeader(http.Request)
        : Principal?.FindFirst("tenant")?.Value;

    public IReadOnlySet<string> Roles =>
        Principal?.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? [];

    public string? CallingService => Principal?.FindFirst("calling_service")?.Value;

    public string? OnBehalfOfUser => Principal?.FindFirst("on_behalf_of")?.Value;

    /// <summary>The <c>X-Tenant-Id</c> header when it looks like a tenant ID; otherwise null.</summary>
    internal static string? ReadTenantHeader(HttpRequest request)
    {
        var value = request.Headers[ApiKeyDefaults.TenantHeader].ToString().Trim();
        return SafeId().IsMatch(value) ? value : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z")]
    private static partial Regex SafeId();
}
