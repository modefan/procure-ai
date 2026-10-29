using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// Limits the request body of an upload to <c>Assistant:Uploads:MaxBytes</c> plus <see cref="FormOverheadBytes"/> (the multipart
/// boundaries and the title), before anything reads it: a declared <c>Content-Length</c> over the limit is answered 413 at once, and the
/// server's own body limit (Kestrel: 30 MB by default) is set to it, so a chunked body is cut off there too. Runs before the
/// <c>[Idempotent]</c> filter, which buffers the body to hash it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadSizeLimitAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    /// <summary>Room for the multipart framing and the other form fields.</summary>
    public const long FormOverheadBytes = 64 * 1024;

    /// <summary>Before the idempotency filter (order 0) and every other resource filter.</summary>
    public int Order => int.MinValue + 100;

    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(serviceProvider.GetRequiredService<IOptions<AssistantOptions>>().Value.Uploads.MaxBytes + FormOverheadBytes, Order);

    private sealed class Filter(long limit, int order) : IResourceFilter, IOrderedFilter
    {
        public int Order => order;

        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var http = context.HttpContext;
            if (http.Request.ContentLength > limit)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = $"The upload is larger than {limit / (1024 * 1024)} MB.",
                })
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge,
                    ContentTypes = { "application/problem+json" },
                };
                return;
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            {
                feature.MaxRequestBodySize = limit;
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
