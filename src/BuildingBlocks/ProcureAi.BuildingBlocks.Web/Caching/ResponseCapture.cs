using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcureAi.BuildingBlocks.Web.Caching;

internal static class ResponseCapture
{
    /// <summary>
    /// Runs the rest of the pipeline with the response body buffered, then copies it to the real response and
    /// returns what was sent. If the pipeline threw, nothing is sent (the exception handler answers the caller)
    /// and a generic 500 is returned for anyone sharing this result.
    /// </summary>
    public static async Task<StoredResponse> RunAsync(
        ResourceExecutingContext context, ResourceExecutionDelegate next, string? requestHash)
    {
        var response = context.HttpContext.Response;
        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        ResourceExecutedContext executed;
        try
        {
            executed = await next();
            await response.BodyWriter.FlushAsync();
        }
        finally
        {
            response.Body = original;
        }

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return StoredResponse.InternalError(requestHash);
        }

        var body = buffer.ToArray();
        var location = response.Headers.Location.ToString();
        var etag = response.Headers.ETag.ToString();
        var stored = new StoredResponse(
            response.StatusCode,
            response.ContentType,
            location.Length > 0 ? location : null,
            etag.Length > 0 ? etag : null,
            body,
            DateTimeOffset.UtcNow,
            requestHash);
        if (body.Length > 0)
        {
            response.ContentLength = body.Length;
            await original.WriteAsync(body, context.HttpContext.RequestAborted);
        }

        return stored;
    }

    /// <summary>503 with <c>Retry-After</c>, for callers that waited too long.</summary>
    public static IActionResult Busy(HttpContext http, int retryAfterSeconds)
    {
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(StatusCodes.Status503ServiceUnavailable, "The request is still being processed. Try again shortly.");
    }

    public static IActionResult Problem(int status, string title) =>
        new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
}
