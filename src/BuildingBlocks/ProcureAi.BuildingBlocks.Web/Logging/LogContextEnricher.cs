using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.BuildingBlocks.Web.Logging;

/// <summary>Adds service name, trace ID, tenant and user ID (IDs only, never personal data) to every log event.</summary>
internal sealed class LogContextEnricher(string serviceName, IHttpContextAccessor accessor) : ILogEventEnricher
{
    private const string TenantClaim = "tenant_id";
    private const string UserClaim = "sub";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        Add(logEvent, propertyFactory, "Service", serviceName);
        Add(logEvent, propertyFactory, "TraceId", Activity.Current?.TraceId.ToString());

        var user = accessor.HttpContext?.User;
        Add(logEvent, propertyFactory, "TenantId", user?.FindFirst(TenantClaim)?.Value);
        Add(logEvent, propertyFactory, "UserId", user?.FindFirst(UserClaim)?.Value);
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value));
        }
    }
}
