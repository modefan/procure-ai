using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Diagnostics;

/// <summary>
/// Sends <c>X-Trace-Id</c> on every response and, when enabled and asked for, the dev panel headers that filters noted.
/// Headers are written in <c>OnStarting</c> because the exception handler clears them before writing its error. A failure there is
/// logged and swallowed (final review F4): the dev panel is a diagnostic aid and must never fail the response it decorates.
/// </summary>
public sealed partial class DevPanelMiddleware(RequestDelegate next, IOptions<DevPanelOptions> options, ILogger<DevPanelMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var wanted = options.Value.Enabled && DevPanelHeaders.IsRequested(context.Request);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            // A proxied response keeps the trace ID and dev panel headers its service sent (the gateway uses this too).
            headers.TryAdd(DevPanelHeaders.TraceId, traceId);
            if (!wanted)
            {
                return Task.CompletedTask;
            }

            try
            {
                foreach (var (name, value) in DevPanelHeaders.Read(context))
                {
                    headers.TryAdd(name, value);
                }

                var tenant = context.RequestServices.GetService<ICurrentUser>()?.TenantId;
                if (!string.IsNullOrEmpty(tenant))
                {
                    headers.TryAdd(DevPanelHeaders.Tenant, tenant);
                }

                var service = context.RequestServices.GetService<ServiceIdentity>();
                if (service is not null)
                {
                    headers.TryAdd(DevPanelHeaders.Instance, service.InstanceId);
                }
            }
            catch (Exception exception)
            {
                LogHeadersFailed(logger, exception, traceId);
            }

            return Task.CompletedTask;
        });

        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the dev panel headers of trace {TraceId}")]
    private static partial void LogHeadersFailed(ILogger logger, Exception exception, string traceId);
}
