using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace ProcureAi.BuildingBlocks.Web.Correlation;

/// <summary>
/// Reads <c>X-Correlation-Id</c> or creates one (the current trace ID), echoes it on the response
/// and adds it to the log context.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";

    /// <summary><see cref="HttpContext.Items"/> key holding the correlation ID, for code that sends messages.</summary>
    public const string ItemKey = "ProcureAi.CorrelationId";

    /// <summary>Longest accepted incoming correlation ID.</summary>
    private const int MaxLength = 128;

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = supplied is { Length: > 0 and <= MaxLength } && SafeId().IsMatch(supplied)
            ? supplied
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        // OnStarting, because the exception handler clears the response headers before writing its error.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (LogContext.PushProperty(LogProperty, correlationId))
        {
            await next(context);
        }
    }
}
