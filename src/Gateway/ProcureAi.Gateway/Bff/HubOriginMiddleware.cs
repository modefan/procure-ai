using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Cross-site WebSocket hijacking guard for the hubs. A browser sends the session cookie with a WebSocket upgrade from any page and the
/// upgrade is a GET (no anti-forgery header; a SignalR client may also skip the negotiate POST), so a WebSocket upgrade to
/// <c>/hubs/**</c> whose <c>Origin</c> is neither the gateway's own nor in <c>Hubs:AllowedOrigins</c> is refused with 403 before it is
/// proxied. Browsers always send <c>Origin</c> on a WebSocket; a request without one is not from a browser page and has to bring its own
/// credentials or cookie anyway.
/// </summary>
public sealed class HubOriginMiddleware(RequestDelegate next, IOptions<HubOptions> options)
{
    public const string HubsPath = "/hubs";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(HubsPath) && IsWebSocketUpgrade(context.Request)
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

    /// <summary>An HTTP/1.1 upgrade (<c>Upgrade: websocket</c>) or an HTTP/2 extended CONNECT.</summary>
    private static bool IsWebSocketUpgrade(HttpRequest request) =>
        request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)
        || HttpMethods.IsConnect(request.Method);

    private bool IsAllowed(HttpRequest request, string origin)
    {
        var own = $"{request.Scheme}://{request.Host}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase)
               || options.Value.AllowedOrigins.Any(allowed => string.Equals(origin, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}
