using Microsoft.AspNetCore.Http.Features;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The browser's log sink (stub). The Angular <c>Logger</c> posts batches of warn and error entries here; the
/// plan 04 observability work forwards them to OpenTelemetry. Until then the batch is read, size-checked and dropped.
/// Anonymous and XSRF-exempt: <c>sendBeacon</c> cannot set headers, and logs may come before the user signs in.
/// The gateway's per-IP rate limit applies as to every other request.
/// </summary>
public static class ClientLogEndpoints
{
    public const string Path = "/api/client-logs";

    /// <summary>A batch is at most a few dozen small entries; anything bigger is not from our Logger.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapClientLogs(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, (Delegate)AcceptAsync).AllowAnonymous().WithOrder(5);
        return app;
    }

    private static async Task<IResult> AcceptAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Log batch too large");
        }

        // Also caps a chunked body that declares no length. Some hosts (the in-memory test server) make the limit read-only.
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        try
        {
            var buffer = new byte[8192];
            var total = 0;
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                total += read;
                if (total > MaxBodyBytes)
                {
                    return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Log batch too large");
                }
            }
        }
        catch (BadHttpRequestException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Log batch too large");
        }

        return Results.Accepted();
    }
}
