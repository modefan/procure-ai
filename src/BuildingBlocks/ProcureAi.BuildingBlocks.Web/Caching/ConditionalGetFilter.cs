using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// Responses that are not a small JSON body are never buffered and never tagged: files (<see cref="FileResult"/>, a
/// <see cref="IResult"/>) and the content types in <see cref="UnbufferedContentTypes"/> (a server-sent event stream must reach the
/// client as it is written; a PDF may be large). They are written straight to the client.
/// </summary>
public sealed class ConditionalGetFilter : IAsyncResourceFilter, IResultFilter
{
    public const string CacheControlValue = "private, no-cache";

    /// <summary>Content types that are streamed to the client as written: no buffering, no ETag.</summary>
    public static readonly IReadOnlyList<string> UnbufferedContentTypes = ["text/event-stream", "application/pdf"];

    private const string UnbufferedItemKey = "ProcureAi.ConditionalGet.Unbuffered";

    /// <summary>Runs just before the result is written: a file or <see cref="IResult"/> result is marked to bypass the buffer.</summary>
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is FileResult or IResult)
        {
            context.HttpContext.Items[UnbufferedItemKey] = true;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

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
        var body = new DecidingBody(http, original, buffer);
        response.Body = body;
        try
        {
            await next();
            await response.BodyWriter.FlushAsync();
        }
        finally
        {
            response.Body = original;
        }

        if (body.PassedThrough || http.Items.ContainsKey(UnbufferedItemKey))
        {
            return;
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

            // A response is tied to the session cookie: some tags (a version such as "7") are the same in every tenant, so a
            // browser must never revalidate an entry stored under another session. (Login and logout also clear the cache.)
            response.Headers.Append(HeaderNames.Vary, HeaderNames.Cookie);

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

    private static bool IsUnbuffered(HttpContext http) =>
        http.Items.ContainsKey(UnbufferedItemKey)
        || (http.Response.ContentType is { } contentType
            && UnbufferedContentTypes.Any(type => contentType.StartsWith(type, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The response body while the action runs. At the first write it looks at what is being sent: a skipped kind of response goes
    /// straight to the client, anything else is collected in the buffer for tagging.
    /// </summary>
    private sealed class DecidingBody(HttpContext http, Stream original, MemoryStream buffer) : Stream
    {
        private bool? _passThrough;

        public bool PassedThrough => _passThrough == true;

        private Stream Target => (_passThrough ??= IsUnbuffered(http)) ? original : buffer;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            if (PassedThrough)
            {
                original.Flush();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            PassedThrough ? original.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] data, int offset, int count) => Target.Write(data, offset, count);
        public override void Write(ReadOnlySpan<byte> data) => Target.Write(data);

        public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken cancellationToken) =>
            Target.WriteAsync(data, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            Target.WriteAsync(data, cancellationToken);
    }
}
