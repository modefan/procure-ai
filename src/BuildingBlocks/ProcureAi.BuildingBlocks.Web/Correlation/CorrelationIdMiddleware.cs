using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace ProcureAi.BuildingBlocks.Web.Correlation;

/// <summary>
/// Reads <c>X-Correlation-Id</c> or creates one (the current trace ID), echoes it on the response
/// and adds it to the log context.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";

    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = !string.IsNullOrWhiteSpace(supplied) && supplied.Length <= MaxLength
            ? supplied
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        // OnStarting, because the exception handler clears the response headers before writing its error.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (LogContext.PushProperty(LogProperty, correlationId))
        {
            await next(context);
        }
    }
}
