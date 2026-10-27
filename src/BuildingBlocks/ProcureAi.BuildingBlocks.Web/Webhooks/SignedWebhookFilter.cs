using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

/// <summary>
/// Verifies the signature of the raw request body before model binding. The body is buffered, so the action still reads it.
/// Every signature failure gives the same 401 (the caller must not learn which check failed); the reason goes to the log
/// and a counter. A body over <see cref="WebhookOptions.MaxBodyBytes"/> gets 413 without being read in full.
/// </summary>
public sealed class SignedWebhookFilter(
    string source, IOptions<WebhookOptions> options, WebhookMetrics metrics, TimeProvider clock, ILogger<SignedWebhookFilter> logger)
    : IAsyncResourceFilter
{
    private const string TooLarge = "too_large";
    private const int BufferThresholdBytes = 64 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var settings = options.Value;
        var request = context.HttpContext.Request;
        if (!settings.Sources.TryGetValue(source, out var configured))
        {
            throw new InvalidOperationException($"No secret configured for webhook source '{source}'.");
        }

        if (request.ContentLength > settings.MaxBodyBytes)
        {
            Reject(context, TooLarge);
            return;
        }

        // No buffer limit here: the read below stops at MaxBodyBytes, so at most that much is ever buffered.
        request.EnableBuffering(BufferThresholdBytes);
        byte[] body;
        try
        {
            body = await ReadAtMostAsync(request.Body, settings.MaxBodyBytes, context.HttpContext.RequestAborted);
        }
        catch (InvalidDataException)
        {
            Reject(context, TooLarge);
            return;
        }

        request.Body.Position = 0;
        var header = request.Headers[WebhookSignature.HeaderName].ToString();
        var result = WebhookSignature.Verify(configured.Secret, header, body, clock.GetUtcNow(), settings.Tolerance);
        if (result != WebhookVerification.Valid)
        {
            Reject(context, ReasonOf(result));
            return;
        }

        await next();
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException("Webhook body too large");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ReasonOf(WebhookVerification result) => result switch
    {
        WebhookVerification.Missing => "missing",
        WebhookVerification.Malformed => "malformed",
        WebhookVerification.Expired => "expired",
        _ => "bad_signature",
    };

    private void Reject(ResourceExecutingContext context, string reason)
    {
        logger.LogWarning("Webhook rejected {Source} {Reason}", source, reason);
        metrics.Rejected(source, reason);
        var (status, title) = reason == TooLarge
            ? (StatusCodes.Status413PayloadTooLarge, "Payload too large")
            : (StatusCodes.Status401Unauthorized, "Unauthorized");
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
    }
}
