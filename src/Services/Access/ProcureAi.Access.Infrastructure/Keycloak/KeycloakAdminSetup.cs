using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Access.Infrastructure.Keycloak;

public static class KeycloakAdminSetup
{
    public const string TokenProviderKey = "keycloak-admin";

    private const string TokenHttpClientName = "keycloak-admin-token";

    /// <summary>
    /// <see cref="IIdentityDirectory"/> on the Keycloak admin API. The options (<c>Keycloak</c> section) are validated at startup.
    /// The admin client has its own token provider, apart from the service's <c>procure-internal</c> one: the two identities
    /// never share a token, and the admin token is never sent to another service. The resilience pipeline of the service
    /// defaults applies to the calls (steps are repeatable, see <see cref="KeycloakAdminClient"/>).
    /// </summary>
    public static IServiceCollection AddKeycloakAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        // The token request itself carries no token.
        services.AddHttpClient(TokenHttpClientName);
        services.AddKeyedSingleton(TokenProviderKey, (sp, _) => new ServiceTokenProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            TokenHttpClientName,
            () => sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.ToCredentials(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<IIdentityDirectory, KeycloakAdminClient>((sp, client) =>
            client.BaseAddress = sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.BaseAddress);
        return services;
    }
}
