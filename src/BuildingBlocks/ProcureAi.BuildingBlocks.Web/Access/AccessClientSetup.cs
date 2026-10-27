using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Permission checks sit in front of every request, so this client fails fast: its own pipeline, from the AccessClient section.
        services.AddProcureAiServiceClient<IAccessDirectory, AccessClient>(AccessServiceName)
            .WithResilienceFrom<AccessClientOptions>();

        // Scoped: it holds the typed client, which must not outlive its handler chain in a singleton.
        services.Replace(ServiceDescriptor.Scoped<IPermissionProvider, AccessPermissionProvider>());
        return services;
    }
}
