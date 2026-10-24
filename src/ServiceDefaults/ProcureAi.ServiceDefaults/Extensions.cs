using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Shared Aspire defaults for every service: OpenTelemetry (OTLP), health checks,
/// service discovery and a standard resilience handler on every HttpClient.
/// </summary>
public static class Extensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    private const string LiveTag = "live";
    private const string ReadyTag = "ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes would otherwise flood the traces.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(LivePath)
                        && !context.Request.Path.StartsWithSegments(ReadyPath))
                .AddHttpClientInstrumentation());

        // The OTLP exporter is switched on by configuration (OTEL_EXPORTER_OTLP_ENDPOINT), which Aspire sets.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Liveness: the process is up. Dependency checks are tagged "ready" by each service.
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag) || registration.Tags.Contains(LiveTag)
        });
        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag)
        });

        return app;
    }
}
