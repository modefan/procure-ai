using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Web.Http;

public static class UserClientSetup
{
    /// <summary>
    /// Registers a typed client for another service of ours that calls it <b>as the signed-in user</b> (the Assistant's model
    /// tools): the base address is <c>http://&lt;serviceName&gt;</c> (service discovery), and
    /// <see cref="UserTokenRelayHandler"/> relays the current request's credential, so the callee checks the user's permissions.
    /// Unlike <c>AddProcureAiServiceClient</c> it uses no service token. Chain <c>.WithResilienceFrom&lt;TOptions&gt;()</c> for
    /// the client's own retries and timeouts. The host needs a <see cref="ServiceIdentity"/> registered.
    /// </summary>
    public static IHttpClientBuilder AddProcureAiUserClient<TClient, TImplementation>(
        this IServiceCollection services, string serviceName)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddHttpContextAccessor();
        var builder = services.AddHttpClient<TClient, TImplementation>(client => client.BaseAddress = new Uri($"http://{serviceName}"));
        // The outermost handler, in front of the defaults (service discovery rewrites the address to the resolved one): the check of
        // where the credential goes must see the address as the caller wrote it.
        return builder.ConfigureAdditionalHttpMessageHandlers((handlers, provider) =>
            handlers.Insert(0, new UserTokenRelayHandler(
                provider.GetRequiredService<IHttpContextAccessor>(), provider.GetRequiredService<ServiceIdentity>(), serviceName)));
    }
}
