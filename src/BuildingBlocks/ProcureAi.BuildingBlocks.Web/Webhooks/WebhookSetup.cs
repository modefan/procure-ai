using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

public static class WebhookSetup
{
    /// <summary>
    /// Binds the <c>Webhooks</c> section (secrets of at least 32 characters) and fails at startup when a configured secret is
    /// too short or an action marked <see cref="SignedWebhookAttribute"/> names a source with no secret.
    /// </summary>
    public static IServiceCollection AddProcureAiWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WebhookMetrics>();
        services.AddHostedService<WebhookSourceCheck>();
        return services;
    }

    private sealed class WebhookSourceCheck(IActionDescriptorCollectionProvider actions, IOptions<WebhookOptions> options) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var missing = actions.ActionDescriptors.Items
                .SelectMany(a => a.FilterDescriptors.Select(f => f.Filter).OfType<SignedWebhookAttribute>())
                .Select(a => a.Source)
                .Where(source => !options.Value.Sources.ContainsKey(source))
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Webhook sources without a secret: {string.Join(", ", missing)}. Set Webhooks:Sources:<source>:Secret.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
