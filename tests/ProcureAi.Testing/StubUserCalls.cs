using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ProcureAi.Testing;

/// <summary>
/// What a stub service saw in one call made <b>as a user</b> (the Assistant's tools relay the caller's credential, plan 05 Task 5b): the
/// relayed <c>Authorization</c> header as it arrived, the API key headers, and the tenant and user the stub read from them.
/// </summary>
public sealed record StubUserCall(
    string Path, string? Authorization, string? ApiKey, string? TenantHeader, string? OnBehalfOf, string? CallingService, string? Tenant,
    string? User);

/// <summary>
/// Who a stub's caller is: the <c>tenant</c> and <c>sub</c> claims of the bearer token (read, not validated: the stub plays a service
/// whose own JWT check is tested elsewhere), or, with the API key, <c>X-Tenant-Id</c> and <c>X-On-Behalf-Of-User</c>.
/// </summary>
public static class StubCaller
{
    public static StubUserCall Read(HttpContext http)
    {
        var authorization = Header(http, "Authorization");
        var apiKey = Header(http, "X-Api-Key");
        var tenantHeader = Header(http, "X-Tenant-Id");
        var onBehalfOf = Header(http, "X-On-Behalf-Of-User");
        string? tenant = null;
        string? user = null;
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = new JsonWebToken(authorization["Bearer ".Length..]);
            tenant = token.TryGetPayloadValue<string>("tenant", out var t) ? t : null;
            user = token.TryGetPayloadValue<string>("sub", out var s) ? s : null;
        }
        else if (apiKey is not null)
        {
            (tenant, user) = (tenantHeader, onBehalfOf);
        }

        return new StubUserCall(
            http.Request.Path + http.Request.QueryString, authorization, apiKey, tenantHeader, onBehalfOf, Header(http, "X-Calling-Service"),
            tenant, user);
    }

    /// <summary>Whether the call carries a credential at all (the real services answer 401 without one).</summary>
    public static bool HasCredential(StubUserCall call) => call.Authorization is not null || call.ApiKey is not null;

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
}
