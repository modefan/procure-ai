using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Correlation;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Logging;
using ProcureAi.BuildingBlocks.Web.OpenApi;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Proxies;
using ProcureAi.BuildingBlocks.Web.RateLimiting;
using ProcureAi.BuildingBlocks.Web.Versioning;

namespace ProcureAi.BuildingBlocks.Web;

/// <summary>The one-line setup every service's Api project calls.</summary>
public static class WebExtensions
{
    public static WebApplicationBuilder AddProcureAiWeb(this WebApplicationBuilder builder, string serviceName)
    {
        builder.AddServiceDefaults();
        builder.AddProcureAiSerilog(serviceName);

        var services = builder.Services;
        services.AddValidatedOptions<CachingOptions>(builder.Configuration, CachingOptions.SectionName);
        services.AddValidatedOptions<RateLimitingOptions>(builder.Configuration, RateLimitingOptions.SectionName);

        // Enums travel as names ("Draft"), in responses, query strings and the OpenAPI document alike. Numbers are
        // numbers only (not "12" strings), so the OpenAPI schema says plain `number` and the generated clients stay simple.
        services.AddControllers().AddJsonOptions(json =>
        {
            json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.AddProcureAiProblemDetails();
        services.AddProcureAiVersioning();
        services.AddProcureAiOpenApiDescriptions(serviceName);
        services.AddProcureAiRateLimiting();
        services.AddProcureAiForwardedHeaders(builder.Configuration);

        services.AddProcureAiAuth(builder.Configuration);

        return builder;
    }

    public static WebApplication UseProcureAiWeb(this WebApplication app)
    {
        app.UseProcureAiForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Authentication first: the limiter partitions signed-in callers by user and tenant.
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapControllers();
        app.MapProcureAiOpenApi();
        app.MapDefaultEndpoints();
        return app;
    }
}
