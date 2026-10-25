using System.Text.Json;

namespace ProcureAi.Gateway.IntegrationTests;

public sealed class RealmTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    [Fact]
    public async Task Tom_gets_token_with_tenant_acme_and_role_requester()
    {
        var claims = await keycloak.LoginAsync("tom@acme.test");

        claims.GetProperty("tenant").GetString().ShouldBe("acme");
        Strings(claims, "roles").ShouldContain("requester");
        Audiences(claims).ShouldContain("procure-api");
    }

    [Theory]
    [InlineData("maria@acme.test", "acme", "manager")]
    [InlineData("dept@acme.test", "acme", "department-head")]
    [InlineData("thandi@acme.test", "acme", "finance")]
    [InlineData("admin@acme.test", "acme", "admin")]
    [InlineData("ben@globex.test", "globex", "manager")]
    public async Task Demo_users_get_their_tenant_and_role(string user, string tenant, string role)
    {
        var claims = await keycloak.LoginAsync(user);

        claims.GetProperty("tenant").GetString().ShouldBe(tenant);
        Strings(claims, "roles").ShouldContain(role);
    }

    [Fact]
    public async Task Committed_realm_file_has_placeholders_instead_of_secrets_and_no_test_client()
    {
        var text = await File.ReadAllTextAsync("procure-realm.json");

        text.ShouldNotContain("Demo!2026");
        text.ShouldContain("${KC_DEMO_PASSWORD}");
        text.ShouldContain("${KC_GATEWAY_SECRET}");
        text.ShouldNotContain(KeycloakFixture.TestClient);
        text.ShouldNotContain("directAccessGrantsEnabled\": true");
    }

    private static string[] Strings(JsonElement claims, string name) =>
        claims.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Audiences(JsonElement claims) =>
        claims.GetProperty("aud").ValueKind == JsonValueKind.Array ? Strings(claims, "aud") : [claims.GetProperty("aud").GetString()!];
}
