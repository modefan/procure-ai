using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcureAi.BuildingBlocks.Web.Caching;

internal static class ResponseCapture
{
    /// <summary>
    /// Runs the rest of the pipeline with the response body buffered and returns what the action produced,
    /// <b>without</b> sending it. The caller stores the result first and then calls <see cref="SendAsync"/>, so
    /// a client that disconnects can never cause a stored result to be lost. If the pipeline threw, nothing is
    /// to be sent (the exception handler answers the caller) and a generic 500 is returned for anyone sharing the result.
    /// </summary>
    public static async Task<Captured> RunAsync(
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
            return new Captured(StoredResponse.InternalError(requestHash), Threw: true);
        }

        var location = response.Headers.Location.ToString();
        var etag = response.Headers.ETag.ToString();
        return new Captured(
            new StoredResponse(
                response.StatusCode,
                response.ContentType,
                location.Length > 0 ? location : null,
                etag.Length > 0 ? etag : null,
                buffer.ToArray(),
                DateTimeOffset.UtcNow,
                requestHash),
            Threw: false);
    }

    /// <summary>Writes a captured body to the client; a client that has gone away is not an error once the result is stored.</summary>
    public static async Task SendAsync(HttpContext http, StoredResponse stored)
    {
        if (stored.Body.Length == 0)
        {
            return;
        }

        try
        {
            http.Response.ContentLength = stored.Body.Length;
            await http.Response.Body.WriteAsync(stored.Body, http.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client disconnected; the result is already stored for its retry.
        }
    }

    public readonly record struct Captured(StoredResponse Response, bool Threw);

    /// <summary>503 with <c>Retry-After</c>, for callers that waited too long.</summary>
    public static IActionResult Busy(HttpContext http, int retryAfterSeconds)
    {
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(StatusCodes.Status503ServiceUnavailable, "The request is still being processed. Try again shortly.");
    }

    public static IActionResult Problem(int status, string title) =>
        new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
}
