using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Gives every successful GET an <c>ETag</c> and <c>Cache-Control: private, no-cache</c> and answers a matching
/// <c>If-None-Match</c> with 304 and no body, so the browser revalidates by itself and a changed answer costs one round trip.
/// The tag is the controller's own (a version ETag such as <c>"7"</c>) when it set one, else a weak tag from the first 16 bytes
/// of the SHA-256 of the body (<c>W/"…"</c>, base64url). Comparison is weak and understands a list and <c>*</c>.
/// Registered as a global MVC filter, so it wraps <see cref="CachedGetFilter"/>: a response replayed from the cache is
/// answered 304 as well. Anything but a 2xx answer to a GET (errors, writes) is left alone.
/// </summary>
public sealed class ConditionalGetFilter : IAsyncResourceFilter
{
    public const string CacheControlValue = "private, no-cache";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            await next();
            return;
        }

        var response = http.Response;
        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await next();
            await response.BodyWriter.FlushAsync();
        }
        finally
        {
            response.Body = original;
        }

        // Only a plain 200 carries a representation we can tag; errors, 201/202/204 and the like pass through untouched.
        if (response.StatusCode == StatusCodes.Status200OK)
        {
            var etag = response.Headers.ETag.ToString();
            if (etag.Length == 0)
            {
                etag = WeakTag(buffer);
                response.Headers.ETag = etag;
            }

            if (!response.Headers.ContainsKey(HeaderNames.CacheControl))
            {
                response.Headers.CacheControl = CacheControlValue;
            }

            if (Matches(http.Request, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.ContentType = null;
                return;
            }
        }

        if (buffer.Length > 0 || response.ContentLength is not null)
        {
            response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(original, http.RequestAborted);
        }
    }

    private static string WeakTag(MemoryStream body)
    {
        var hash = SHA256.HashData(body.GetBuffer().AsSpan(0, (int)body.Length));
        return "W/\"" + Convert.ToBase64String(hash.AsSpan(0, 16)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "\"";
    }

    /// <summary>Weak comparison of <c>If-None-Match</c> (a list of tags or <c>*</c>) with the response tag.</summary>
    private static bool Matches(HttpRequest request, string etag)
    {
        if (!EntityTagHeaderValue.TryParse(etag, out var current))
        {
            return false;
        }

        foreach (var candidate in request.GetTypedHeaders().IfNoneMatch)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }
}
