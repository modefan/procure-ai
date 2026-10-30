using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Status;

public static class StatusSetup
{
    /// <summary>
    /// The status snapshot (plan 06 ruling 9): <see cref="StatusOptions"/> (section <c>Status</c>, validated at startup), the breaker
    /// registry, the snapshot builder and, when <paramref name="publish"/>, the <see cref="StatusPublisher"/> that writes it to Redis.
    /// Wolverine's counts come from <see cref="IWolverineCounts"/> (the messaging building block registers the real one first; otherwise none).
    /// A service adds its own <see cref="IStatusContributor"/>s with <see cref="AddStatusContributor{T}"/>.
    /// </summary>
    public static IServiceCollection AddProcureAiStatus(this IServiceCollection services, IConfiguration configuration, bool publish = true)
    {
        services.AddValidatedOptions<StatusOptions>(configuration, StatusOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICircuitBreakerStates, CircuitBreakerStates>();
        services.TryAddSingleton<IWolverineCounts, NoWolverineCounts>();
        services.TryAddSingleton<StatusSnapshotBuilder>();
        if (publish)
        {
            services.TryAddSingleton<StatusPublisher>();
            services.AddHostedService(provider => provider.GetRequiredService<StatusPublisher>());
        }

        return services;
    }

    /// <summary>Adds a section to this service's snapshot. Resolved in a scope of its own on every snapshot.</summary>
    public static IServiceCollection AddStatusContributor<T>(this IServiceCollection services)
        where T : class, IStatusContributor
    {
        services.AddScoped<IStatusContributor, T>();
        return services;
    }

    /// <summary>Lists a breaker (Closed) before its first state change. Also registers the registry, so a pipeline can report to it.</summary>
    public static IServiceCollection AddKnownCircuitBreaker(this IServiceCollection services, string name)
    {
        services.TryAddSingleton<ICircuitBreakerStates, CircuitBreakerStates>();
        services.AddSingleton(new KnownCircuitBreaker(name));
        return services;
    }
}
