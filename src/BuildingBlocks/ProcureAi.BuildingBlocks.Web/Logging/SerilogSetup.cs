using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ProcureAi.BuildingBlocks.Web.Logging;

internal static class SerilogSetup
{
    /// <summary>
    /// Serilog configured from the <c>Serilog</c> section (levels, sinks incl. OpenTelemetry, enrichers).
    /// Code adds only the context enricher and the header masking policy. Serilog replaces the logger factory,
    /// so logs reach the Aspire dashboard through the configured OpenTelemetry sink, not twice.
    /// </summary>
    public static WebApplicationBuilder AddProcureAiSerilog(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSerilog((services, logger) =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            logger
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.With(new LogContextEnricher(serviceName, services.GetRequiredService<IHttpContextAccessor>()))
                .Destructure.With<HeaderMaskingPolicy>();

            // A service without a Serilog section must never log nothing at all.
            if (!configuration.GetSection("Serilog").Exists())
            {
                logger.WriteTo.Console();
            }
        }, preserveStaticLogger: true);
        return builder;
    }
}
