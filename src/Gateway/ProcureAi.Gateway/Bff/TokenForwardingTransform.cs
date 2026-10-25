using Microsoft.AspNetCore.Authentication;
using ProcureAi.BuildingBlocks.Web.Correlation;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// What the gateway does to every request on its way to a service:
/// <list type="bullet">
/// <item>the browser's cookies are never forwarded (the session lives in Redis; services are stateless);</item>
/// <item>a call with its own credentials (<c>X-Api-Key</c>, or a bearer token without a session) goes through
/// unchanged and never gets a token added;</item>
/// <item>a call on the session loses any incoming <c>Authorization</c> header and gets the session's access token
/// as <c>Authorization: Bearer</c>, on protected routes only (the web app and public documents get no token);</item>
/// <item>the correlation ID travels along, and <c>X-Forwarded-For</c> is set to the client the gateway saw.</item>
/// </list>
/// </summary>
public static class TokenForwardingTransform
{
    public static void Configure(TransformBuilderContext builder)
    {
        // Set, not append: a client-supplied X-Forwarded-For must not reach the services' rate limiter.
        builder.UseDefaultForwarders = false;
        builder.AddXForwarded(ForwardedTransformActions.Set);
        builder.AddRequestTransform(TransformAsync);
    }

    private static async ValueTask TransformAsync(RequestTransformContext context)
    {
        var http = context.HttpContext;
        context.ProxyRequest.Headers.Remove("Cookie");

        if (http.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId) && correlationId is string id)
        {
            context.ProxyRequest.Headers.Remove(CorrelationIdMiddleware.HeaderName);
            context.ProxyRequest.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, id);
        }

        if (BffSchemes.UsesPassThrough(http.Request) || !IsProtectedRoute(http))
        {
            return;
        }

        // A browser call on a session: the gateway's token replaces whatever Authorization header came in.
        context.ProxyRequest.Headers.Remove("Authorization");
        if (http.User.Identity?.IsAuthenticated == true
            && await http.GetTokenAsync(BffSchemes.Cookie, "access_token") is { Length: > 0 } accessToken)
        {
            context.ProxyRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        }
    }

    private static bool IsProtectedRoute(HttpContext http)
    {
        var policy = http.Features.Get<IReverseProxyFeature>()?.Route.Config.AuthorizationPolicy;
        return !string.Equals(policy, "anonymous", StringComparison.OrdinalIgnoreCase);
    }
}
