namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>One Keycloak and one Redis for all gateway tests (each takes a while to start and has a memory limit).</summary>
[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<KeycloakFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "Gateway";
}
