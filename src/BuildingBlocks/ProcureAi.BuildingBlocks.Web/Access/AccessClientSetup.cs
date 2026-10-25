using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Access;

public static class AccessClientSetup
{
    /// <summary>The service name of Access: its address (<c>http://access-api</c>) and the one service that does not use this client.</summary>
    public const string AccessServiceName = "access-api";

    /// <summary>
    /// <see cref="IAccessDirectory"/> (a cached, fail-safe client of the Access service) and, on top of it, the
    /// <see cref="IPermissionProvider"/> that policy-based authorization asks. Retries and timeouts of the call come from
    /// the <c>AccessClient</c> section. Needs <c>Auth:Internal</c> (the service logs in as itself); the caller registers
    /// FusionCache.
    /// </summary>
    public static IServiceCollection AddProcureAiAccessClient(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddValidatedOptions<AccessClientOptions>(configuration, AccessClientOptions.SectionName);
        services.TryAddSingleton<AccessCacheGeneration>();
        // A host without messaging (the gateway) acts as itself; hosts with messaging registered the real ones already.
        services.TryAddSingleton(new ServiceIdentity(serviceName));
        services.TryAddSingleton<ICallerContextAccessor, SystemCallerContextAccessor>();

        // The service defaults give every client one shared standard resilience pipeline (retries of seconds, 30 s timeouts).
        // Permission checks sit in front of every request, so this client gets its own, from the AccessClient section.
        // RemoveAllResilienceHandlers is marked experimental by Microsoft; it is the supported way to replace the defaults' pipeline.
#pragma warning disable EXTEXP0001
        var pipeline = services.AddProcureAiServiceClient<IAccessDirectory, AccessClient>(AccessServiceName)
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler();
#pragma warning restore EXTEXP0001
        services.AddOptions<HttpStandardResilienceOptions>(pipeline.PipelineName)
            .Configure<IOptions<AccessClientOptions>>((resilience, access) =>
            {
                var settings = access.Value;
                resilience.Retry.MaxRetryAttempts = settings.RetryCount;
                resilience.Retry.Delay = settings.RetryDelay;
                resilience.AttemptTimeout.Timeout = settings.AttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = settings.TotalTimeout;
            });

        // Scoped: it holds the typed client, which must not outlive its handler chain in a singleton.
        services.Replace(ServiceDescriptor.Scoped<IPermissionProvider, AccessPermissionProvider>());
        return services;
    }
}
