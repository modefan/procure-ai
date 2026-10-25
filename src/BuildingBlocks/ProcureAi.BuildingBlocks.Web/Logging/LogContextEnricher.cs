using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.BuildingBlocks.Web.Logging;

/// <summary>Adds service name, trace ID, tenant and user ID (IDs only, never personal data) to every log event.</summary>
internal sealed class LogContextEnricher(string serviceName, IHttpContextAccessor accessor) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        Add(logEvent, propertyFactory, "Service", serviceName);
        Add(logEvent, propertyFactory, "TraceId", Activity.Current?.TraceId.ToString());

        // Through ICurrentUser, so admin API key calls log the tenant from X-Tenant-Id and the user claude-admin.
        var user = new CurrentUser(accessor);
        if (user.IsAuthenticated)
        {
            Add(logEvent, propertyFactory, "TenantId", user.TenantId);
            Add(logEvent, propertyFactory, "UserId", user.UserId);
        }
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value));
        }
    }
}
