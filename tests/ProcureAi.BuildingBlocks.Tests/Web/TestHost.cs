using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using ProcureAi.BuildingBlocks.Web;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Captures Serilog events in memory; registered in DI so ReadFrom.Services picks it up.</summary>
public sealed class CapturingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent)
    {
        lock (Events)
        {
            Events.Add(logEvent);
        }
    }
}

public static class TestHost
{
    public static async Task<WebApplication> StartAsync(
        Dictionary<string, string?>? config = null, CapturingSink? sink = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config ?? []);
        if (sink is not null)
        {
            builder.Services.AddSingleton<ILogEventSink>(sink);
        }

        builder.AddProcureAiWeb("test-service");
        builder.Services.AddControllers().AddApplicationPart(typeof(TestController).Assembly);

        var app = builder.Build();
        // Stands in for authentication (Task 5): the test supplies the user through headers.
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
            {
                context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("tenant", tenant.ToString()),
                    new System.Security.Claims.Claim("sub", context.Request.Headers["X-Test-User"].ToString()),
                ], "test"));
            }

            return next(context);
        });
        app.UseProcureAiWeb();
        await app.StartAsync();
        return app;
    }
}
