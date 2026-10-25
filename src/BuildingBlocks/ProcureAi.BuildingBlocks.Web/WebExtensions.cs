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

        // Enums travel as names ("Draft"), in responses, query strings and the OpenAPI document alike.
        services.AddControllers().AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddProcureAiProblemDetails();
        services.AddProcureAiVersioning();
        services.AddProcureAiOpenApiDescriptions();
        services.AddProcureAiRateLimiting();

        services.AddProcureAiAuth(builder.Configuration);

        return builder;
    }

    public static WebApplication UseProcureAiWeb(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();
        app.MapProcureAiOpenApi();
        app.MapDefaultEndpoints();
        return app;
    }
}
