using Microsoft.AspNetCore.Hosting;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// The real Access API (its Program) on a test server, against the shared Postgres, RabbitMQ and Redis. The Keycloak admin
/// settings point nowhere unless a test passes a Keycloak (then they point at it, with <paramref name="demoOrganization"/>
/// as the demo company).
/// </summary>
public class AccessApiFactory(AccessInfraFixture infra, KeycloakFixture? keycloak = null, string? demoOrganization = null)
    : ServiceApiFactory<Program>("accessdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    public const string InternalClientId = "procure-internal";

    /// <summary>The cost center self-registered users get (the demo seed creates it in the real demo tenant).</summary>
    public const string SandboxCostCenter = "DEMO-100";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:InternalClientId", InternalClientId);
        builder.UseSetting("Keycloak:AdminBaseUrl", keycloak?.BaseUrl ?? "http://keycloak.invalid");
        builder.UseSetting("Keycloak:Realm", KeycloakFixture.Realm);
        builder.UseSetting("Keycloak:DemoOrganization", demoOrganization ?? "demo");
        builder.UseSetting("Keycloak:AdminClientId", KeycloakFixture.AccessAdminClient);
        builder.UseSetting("Keycloak:AdminClientSecret", keycloak?.AccessAdminSecret ?? "not-used");
    }
}
