using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Cross-site WebSocket hijacking guard for the hubs. A browser sends the session cookie with a WebSocket upgrade from any page and the
/// upgrade is a GET (no anti-forgery header; a SignalR client may also skip the negotiate POST), so a WebSocket upgrade to
/// <c>/hubs/**</c> whose <c>Origin</c> is neither the gateway's own nor in <c>Hubs:AllowedOrigins</c> is refused with 403 before it is
/// proxied. Browsers always send <c>Origin</c> on a WebSocket; a request without one is not from a browser page and has to bring its own
/// credentials or cookie anyway.
/// <para>
/// The same holds for an HTTP/2 WebSocket (RFC 8441 extended CONNECT), which Chrome uses when it talks HTTP/2 straight to the gateway (under
/// the AppHost). The anti-forgery check (<see cref="XsrfMiddleware"/>) skips both kinds of hub WebSocket because this check covers them: a
/// CONNECT is not a safe method, and refusing it made SignalR fall back to long polling.
/// </para>
/// </summary>
public sealed class HubOriginMiddleware(RequestDelegate next, IOptions<HubOptions> options)
{
    public const string HubsPath = "/hubs";

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHubWebSocket(context.Request)
            && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsAllowed(context.Request, origin))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Origin not allowed",
                detail: "WebSockets to the hubs are accepted from the app's own origin only.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>A WebSocket to <c>/hubs/**</c>: an HTTP/1.1 upgrade (<c>Upgrade: websocket</c>) or an HTTP/2 extended CONNECT.</summary>
    public static bool IsHubWebSocket(HttpRequest request) =>
        request.Path.StartsWithSegments(HubsPath)
        && (request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsConnect(request.Method));

    private bool IsAllowed(HttpRequest request, string origin)
    {
        // Behind a proxy that forwards a WebSocket upgrade with X-Forwarded-Proto: wss (Traefik does), the scheme is wss, but the page
        // that opened the socket is https://<host>: a browser origin is always http or https.
        var scheme = request.Scheme.ToLowerInvariant() switch
        {
            "wss" => Uri.UriSchemeHttps,
            "ws" => Uri.UriSchemeHttp,
            var other => other,
        };
        var own = $"{scheme}://{request.Host}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase)
               || options.Value.AllowedOrigins.Any(allowed => string.Equals(origin, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}
