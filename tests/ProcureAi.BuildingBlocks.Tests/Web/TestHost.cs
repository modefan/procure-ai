using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
        Dictionary<string, string?>? config = null, CapturingSink? sink = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Authority"] = TestAuth.Issuer,
            ["Auth:Audience"] = TestAuth.Audience,
            ["Auth:AdminApiKeySha256"] = TestAuth.ApiKeySha256,
            ["Auth:OpenApi:AuthorizationUrl"] = TestAuth.Issuer + "/auth",
            ["Auth:OpenApi:TokenUrl"] = TestAuth.Issuer + "/token",
            ["Auth:OpenApi:Scopes:openid"] = "OpenID Connect",
        };
        foreach (var (key, value) in config ?? [])
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        // Validate tokens against the local test key, without discovery documents or any network call.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.TokenValidationParameters.IssuerSigningKey = TestAuth.SigningKey;
            options.TokenValidationParameters.ValidIssuer = TestAuth.Issuer;
        });
        if (sink is not null)
        {
            builder.Services.AddSingleton<ILogEventSink>(sink);
        }

        builder.AddProcureAiWeb("test-service");
        builder.Services.AddControllers().AddApplicationPart(typeof(TestController).Assembly);

        // Tests that are not about Access use a fixed matrix; the Access client tests replace it.
        builder.Services.AddSingleton<ProcureAi.BuildingBlocks.Web.Auth.IPermissionProvider, TestMatrixPermissionProvider>();

        configure?.Invoke(builder);

        var app = builder.Build();
        // Lets tests choose the connection address (TestServer has none), as a proxy or client would present it.
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var ip))
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip.ToString());
            }

            return next(context);
        });
        // Lets log tests supply an authenticated user without a token: the user through headers.
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
