using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Http;

public static class UserClientSetup
{
    /// <summary>
    /// Registers a typed client for another service of ours that calls it <b>as the signed-in user</b> (the Assistant's model
    /// tools): the base address is <c>http://&lt;serviceName&gt;</c> (service discovery), and
    /// <see cref="UserTokenRelayHandler"/> relays the current request's credential, so the callee checks the user's permissions.
    /// Unlike <c>AddProcureAiServiceClient</c> it uses no service token. Chain <c>.WithResilienceFrom&lt;TOptions&gt;()</c> for
    /// the client's own retries and timeouts. The host needs a <see cref="Application.ServiceIdentity"/> registered.
    /// </summary>
    public static IHttpClientBuilder AddProcureAiUserClient<TClient, TImplementation>(
        this IServiceCollection services, string serviceName)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddHttpContextAccessor();
        services.AddTransient<UserTokenRelayHandler>();
        return services.AddHttpClient<TClient, TImplementation>(client => client.BaseAddress = new Uri($"http://{serviceName}"))
            .AddHttpMessageHandler<UserTokenRelayHandler>();
    }
}
