using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Options;

public static class ValidatedOptionsExtensions
{
    /// <summary>
    /// Binds a configuration section to <typeparamref name="T"/>, validates its data annotations
    /// and fails at host start (not on first use) when the configuration is invalid.
    /// </summary>
    public static IServiceCollection AddValidatedOptions<T>(
        this IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }
}
