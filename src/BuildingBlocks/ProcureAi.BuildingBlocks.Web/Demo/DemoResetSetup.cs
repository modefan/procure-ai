using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Demo;

public static class DemoResetSetup
{
    /// <summary>
    /// Binds <see cref="DemoResetOptions"/> (section <c>DemoReset</c>) and fails at host start when it is invalid: the ranges on the options,
    /// and a quiet period shorter than the drain timeout (else the drain could never succeed). Every service gets it through
    /// <c>AddProcureAiWeb</c>, whether its reset is on or off.
    /// </summary>
    public static IServiceCollection AddProcureAiDemoResetOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DemoResetOptions>()
            .Bind(configuration.GetSection(DemoResetOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.QuietPeriod < o.DrainTimeout, "DemoReset:QuietPeriod must be shorter than DemoReset:DrainTimeout.")
            .Validate(o => o.DrainPollInterval < o.QuietPeriod, "DemoReset:DrainPollInterval must be shorter than DemoReset:QuietPeriod.")
            .ValidateOnStart();
        return services;
    }
}
