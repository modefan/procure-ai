using Microsoft.AspNetCore.Antiforgery;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Anti-forgery for the browser session. Every state-changing call that rides on the
/// session cookie must carry the <c>X-XSRF-TOKEN</c> header, which the app copies from the <c>XSRF-TOKEN</c> cookie
/// (Angular does this by itself). Calls with their own credentials (API key, bearer token) are not cookie-borne
/// and need no token.
/// </summary>
public sealed class XsrfMiddleware(RequestDelegate next)
{
    public const string CookieName = "XSRF-TOKEN";
    public const string HeaderName = "X-XSRF-TOKEN";

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (Applies(context.Request) && context.User.Identity?.IsAuthenticated == true
            && !await antiforgery.IsRequestValidAsync(context))
        {
            await Rejection().ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>The answer to a state-changing call without a valid anti-forgery token (also used by the anonymous demo login).</summary>
    public static IResult Rejection() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Missing or invalid anti-forgery token",
        detail: $"Send the value of the {CookieName} cookie in the {HeaderName} header.");

    private static bool Applies(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
          || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && !BffSchemes.UsesPassThrough(request)
        // sendBeacon cannot set headers; the endpoint changes nothing for the user.
        && !ClientLogEndpoints.IsClientLogPath(request.Path);
}
