using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
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

    /// <summary>The name of both the ActivitySource and the Meter of Wolverine.</summary>
    public const string WolverineTelemetryName = "Wolverine";

    /// <summary>
    /// The services' own business meters (e.g. <c>ProcureAi.Audit</c>: changes made outside the app): every meter whose name starts
    /// with <c>ProcureAi.</c> is exported, so a service adds a metric without touching this file.
    /// </summary>
    public const string BusinessMeters = "ProcureAi.*";

    /// <summary>
    /// The services' own activity sources (e.g. <c>ProcureAi.Assistant.AI</c>: every model call, from Microsoft.Extensions.AI's
    /// OpenTelemetry middleware): every source whose name starts with <c>ProcureAi.</c> is traced, like the meters.
    /// </summary>
    public const string BusinessSources = "ProcureAi.*";

    /// <summary>
    /// Polly's meter: the <c>resilience.polly.*</c> metrics of every resilience pipeline (retries, timeouts, a circuit breaker
    /// opening and closing, e.g. Purchasing's supplier client).
    /// </summary>
    public const string ResilienceMeter = "Polly";

    /// <summary>Health checks with this tag decide <c>/health/ready</c> (dependencies); services add theirs.</summary>
    public const string ReadyTag = "ready";

    private const string LiveTag = "live";

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
                .AddRuntimeInstrumentation()
                // Registering a meter or source for a library a service does not use is harmless: it just never emits.
                .AddMeter(WolverineTelemetryName)
                .AddMeter(BusinessMeters)
                .AddMeter(ResilienceMeter)
                .AddNpgsqlInstrumentation()
                .AddFusionCacheInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(BusinessSources)
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes would otherwise flood the traces.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(LivePath)
                        && !context.Request.Path.StartsWithSegments(ReadyPath);
                    // Query values may be credentials (the tenant switch ticket): only the names reach the trace. Same format as the
                    // instrumentation's own redaction (with the leading ?), our mask.
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        if (request.QueryString.HasValue)
                        {
                            activity.SetTag("url.query", QueryStringRedaction.Redact(request.QueryString.Value));
                        }
                    };
                })
                .AddHttpClientInstrumentation()
                // Handled messages, outbox sends and transport hops, linked to the trace of the request that caused them.
                .AddSource(WolverineTelemetryName)
                .AddNpgsql()
                // EF Core has no ActivitySource of its own; its SQL shows as Npgsql command spans.
                // Resolves the IConnectionMultiplexer from DI when one is registered, and does nothing otherwise.
                .AddRedisInstrumentation()
                .AddFusionCacheInstrumentation());

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
        }).AllowAnonymous();
        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag)
        }).AllowAnonymous();

        return app;
    }
}
