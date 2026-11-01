using Microsoft.AspNetCore.Authorization;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Correlation;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Health;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Logging;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Proxies;
using ProcureAi.BuildingBlocks.Web.RateLimiting;
using ProcureAi.Gateway.Bff;
using ProcureAi.Gateway.Configuration;
using ProcureAi.Gateway.Swagger;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.Gateway;

/// <summary>
/// The composition root of the gateway: the YARP reverse proxy, the BFF login with its Redis session, the
/// rate limiter and Swagger UI. Configuration comes from appsettings, user secrets and the environment.
/// </summary>
public static class GatewayApp
{
    public const string ServiceName = "gateway";

    /// <param name="args">Command line.</param>
    /// <param name="configure">Lets the integration tests replace configuration and services.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.AddServiceDefaults();
        builder.AddProcureAiSerilog(ServiceName);

        var configuration = builder.Configuration;
        var services = builder.Services;
        services.AddValidatedOptions<OidcOptions>(configuration, OidcOptions.SectionName);
        services.AddValidatedOptions<BffSessionOptions>(configuration, BffSessionOptions.SectionName);
        services.AddValidatedOptions<RegistrationOptions>(configuration, RegistrationOptions.SectionName);
        services.AddValidatedOptions<DemoLoginOptions>(configuration, DemoLoginOptions.SectionName);
        services.AddValidatedOptions<HubOptions>(configuration, HubOptions.SectionName);
        services.AddValidatedOptions<DevPanelOptions>(configuration, DevPanelOptions.SectionName);
        services.AddValidatedOptions<ClientLogOptions>(configuration, ClientLogOptions.SectionName);
        services.AddSingleton<ClientLogRateLimiter>();
        services.AddValidatedOptions<RateLimitingOptions>(configuration, RateLimitingOptions.SectionName);
        services.AddProcureAiForwardedHeaders(configuration);
        services.AddProcureAiCorrelationPropagation();
        services.AddProcureAiProblemDetails();
        // The gateway has no API key of its own to check, and has the client IP only: behind it every user is one IP.
        services.AddProcureAiRateLimiting(RateLimitPartitioning.ClientIp);
        // Demo login and tenant switch: a much lower limit of their own, on top of the global one (each costs a Keycloak password grant).
        services.AddValidatedOptions<DemoEntryRateLimitOptions>(configuration, DemoEntryRateLimitOptions.SectionName);
        services.AddDemoEntryRateLimit();
        services.AddGatewaySwagger(configuration);

        AddRedis(builder);
        services.AddRedisReadiness();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        // Permissions come from Access, cached in memory for AccessClient:CacheDuration (1 s): the gateway has no message bus,
        // so it cannot hear AccessChanged and relies on the short cache instead. The browser reads /bff/user again on the
        // access-matrix push and once more cache.trailingReloadMs (2 s) later; the cache must expire well before that second read
        // (PermissionRefreshSettingsTests), so a matrix change reaches the menu within seconds.
        services.AddFusionCache();
        services.AddProcureAiAccessClient(configuration, ServiceName);
        services.AddBffAuthentication();
        services.AddProcureAiServiceClient<IRegistrationClient, RegistrationClient>(AccessClientSetup.AccessServiceName);
        services.AddProcureAiServiceClient<IUserTenantsClient, UserTenantsClient>(AccessClientSetup.AccessServiceName);
        services.AddScoped<DemoRegistration>();
        services.AddSingleton<DemoSignIn>();

        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(TokenForwardingTransform.Configure);

        var app = builder.Build();

        app.UseProcureAiForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<DevPanelMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // By client IP, before authentication: it also protects the login endpoints.
        app.UseRateLimiter();
        app.UseGatewaySwagger();
        app.UseAuthentication();
        // WebSockets to /hubs: the Origin check stands in for the anti-forgery header a GET upgrade cannot carry.
        app.UseMiddleware<HubOriginMiddleware>();
        app.UseMiddleware<XsrfMiddleware>();
        app.UseAuthorization();

        app.MapBff();
        app.MapDemoLogin();
        app.MapTenantSwitch();
        app.MapClientLogs();
        app.MapDefaultEndpoints();
        // An unknown /api path is a 404, not the web app's index page. Order: after the proxied API routes (10).
        app.Map("/api/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"))
            .AllowAnonymous().WithOrder(500);
        app.MapReverseProxy();
        return app;
    }

    private static void AddRedis(WebApplicationBuilder builder)
    {
        var redis = builder.Configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            throw new InvalidOperationException("Connection string 'Redis' is required: sessions are kept in Redis.");
        }

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
    }
}
