using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Proxies;

public static class ForwardedHeadersSetup
{
    /// <summary>Binds and validates the <c>ForwardedHeaders</c> section; the trusted proxies come from it and nowhere else.</summary>
    public static IServiceCollection AddProcureAiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ForwardedHeadersSettings>(configuration, ForwardedHeadersSettings.SectionName);
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
            {
                options.ForwardedHeaders = settings.Value.Headers;
                options.ForwardLimit = settings.Value.ForwardLimit;
                // Replace the framework's loopback defaults: only the configured proxies are trusted.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var proxy in settings.Value.ParsedProxies)
                {
                    options.KnownProxies.Add(proxy);
                }

                foreach (var network in settings.Value.ParsedNetworks)
                {
                    options.KnownIPNetworks.Add(network);
                }
            });
        return services;
    }

    /// <summary>Applies the configured <c>X-Forwarded-*</c> headers from trusted proxies. Does nothing when none are configured.</summary>
    public static WebApplication UseProcureAiForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value.HasTrustedProxies)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
