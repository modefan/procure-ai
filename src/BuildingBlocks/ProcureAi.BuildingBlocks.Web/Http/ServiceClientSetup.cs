using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Http;

public static class ServiceClientSetup
{
    /// <summary>
    /// Registers a typed client for another service of ours. Its base address is <c>http://&lt;serviceName&gt;</c>, resolved
    /// by Aspire service discovery (and Kubernetes DNS when deployed); resilience (retry, timeout, circuit breaker) comes
    /// from the service defaults. Every call is made as this service (<see cref="ServiceTokenHandler"/>), on behalf of the
    /// current caller. Needs <c>Auth:Internal</c> configured, which is validated at startup.
    /// </summary>
    public static IHttpClientBuilder AddProcureAiServiceClient<TClient, TImplementation>(
        this IServiceCollection services, string serviceName)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddOptions<InternalClientOptions>()
            .BindConfiguration(InternalClientOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ServiceTokenProvider>();
        services.TryAddTransient<ServiceTokenHandler>();
        // The token request itself must not carry a token: a named client without the handler.
        services.AddHttpClient(ServiceTokenProvider.HttpClientName);

        return services.AddHttpClient<TClient, TImplementation>(client => client.BaseAddress = new Uri($"http://{serviceName}"))
            .AddHttpMessageHandler<ServiceTokenHandler>();
    }
}
