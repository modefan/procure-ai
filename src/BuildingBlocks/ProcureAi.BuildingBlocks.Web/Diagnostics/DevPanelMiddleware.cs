using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Diagnostics;

/// <summary>
/// Sends <c>X-Trace-Id</c> on every response and, when enabled and asked for, the dev panel headers that filters noted.
/// Headers are written in <c>OnStarting</c> because the exception handler clears them before writing its error.
/// </summary>
public sealed class DevPanelMiddleware(RequestDelegate next, IOptions<DevPanelOptions> options)
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
            if (wanted)
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

            return Task.CompletedTask;
        });

        await next(context);
    }
}
