using System.Text.Json;

namespace ProcureAi.Gateway.IntegrationTests;

[Collection(GatewayCollection.Name)]
public sealed class RealmTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task Access_token_carries_the_name_username_and_email_the_gateway_shows()
    {
        var claims = await keycloak.LoginAsync("tom@acme.test");

        claims.GetProperty("name").GetString().ShouldBe("Tom Mokoena");
        claims.GetProperty("preferred_username").GetString().ShouldBe("tom@acme.test");
        claims.GetProperty("email").GetString().ShouldBe("tom@acme.test");
    }

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

    [Theory]
    [InlineData("tom@acme.test", "requester")]
    [InlineData("maria@acme.test", "manager")]
    [InlineData("dept@acme.test", "department-head")]
    [InlineData("thandi@acme.test", "finance")]
    [InlineData("admin@acme.test", "admin")]
    [InlineData("ben@globex.test", "manager")]
    public async Task Each_demo_user_holds_exactly_their_own_business_role(string user, string role)
    {
        var claims = await keycloak.LoginAsync(user);

        Strings(claims, "roles").Where(BusinessRoles.Contains).ShouldBe([role]);
    }

    [Fact]
    public async Task Admin_does_not_get_the_requester_role()
    {
        var claims = await keycloak.LoginAsync("admin@acme.test");

        Strings(claims, "roles").ShouldNotContain("requester");
    }

    [Fact]
    public async Task Committed_realm_file_has_only_placeholder_credentials_and_no_direct_access_client()
    {
        var realm = JsonDocument.Parse(await File.ReadAllTextAsync("procure-realm.json")).RootElement;

        var passwords = realm.GetProperty("users").EnumerateArray()
            .SelectMany(u => u.GetProperty("credentials").EnumerateArray())
            .Select(c => c.GetProperty("value").GetString()!).ToArray();
        passwords.ShouldNotBeEmpty();
        passwords.ShouldAllBe(v => v.StartsWith("${") && v.EndsWith('}'));

        var clients = realm.GetProperty("clients").EnumerateArray().ToArray();
        clients.ShouldNotContain(c => c.GetProperty("clientId").GetString() == KeycloakFixture.TestClient);
        clients.Count(c => c.TryGetProperty("directAccessGrantsEnabled", out var d) && d.GetBoolean()).ShouldBe(0);
        clients.Single(c => c.GetProperty("clientId").GetString() == "procure-gateway")
            .GetProperty("secret").GetString().ShouldBe("${KC_GATEWAY_SECRET}");
    }

    private static readonly string[] BusinessRoles = ["requester", "manager", "department-head", "finance", "admin"];

    private static string[] Strings(JsonElement claims, string name) =>
        claims.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Audiences(JsonElement claims) =>
        claims.GetProperty("aud").ValueKind == JsonValueKind.Array ? Strings(claims, "aud") : [claims.GetProperty("aud").GetString()!];
}
