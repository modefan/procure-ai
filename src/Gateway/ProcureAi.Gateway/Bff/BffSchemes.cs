using Microsoft.AspNetCore.Http;

namespace ProcureAi.Gateway.Bff;

/// <summary>Authentication scheme names and the rule that picks between them.</summary>
public static class BffSchemes
{
    /// <summary>Policy scheme: picks <see cref="Cookie"/> or <see cref="PassThrough"/> per request.</summary>
    public const string Smart = "Smart";

    /// <summary>The server-side session behind the <c>__Host-procure</c> cookie.</summary>
    public const string Cookie = "Cookies";

    /// <summary>The login at the identity provider.</summary>
    public const string Oidc = "oidc";

    /// <summary>
    /// A call that brings its own credentials (<c>X-Api-Key</c> or a bearer token): the gateway lets it through
    /// unchanged and the service validates it. Not a login of the gateway.
    /// </summary>
    public const string PassThrough = "PassThrough";

    public const string SessionCookieName = "__Host-procure";
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// True when the call brings its own credentials and is not carried by the session cookie. Applies to proxied API
    /// calls only: the /bff endpoints work on the session alone, whatever headers come with them. An explicit
    /// <c>Authorization: Bearer</c> header wins over a session (Swagger UI tests with its own OAuth2 token on the
    /// gateway origin; a custom header cannot be forged by another site), so such a call is not cookie-authenticated.
    /// </summary>
    public static bool UsesPassThrough(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/bff"))
        {
            return false;
        }

        // Log posts read the session themselves, read-only: authenticating by cookie here would slide the session's expiry and
        // refresh its tokens, so background logging would keep an idle session alive.
        if (request.Path.Equals(ClientLogEndpoints.Path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.Headers.ContainsKey(ApiKeyHeader)
               || request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }
}
