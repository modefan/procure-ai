using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.Contracts;

namespace ProcureAi.Gateway.IntegrationTests;

[Collection(GatewayCollection.Name)]
public sealed class RealmTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task Access_token_carries_the_username_but_no_name_or_email()
    {
        var claims = await keycloak.LoginAsync("tom@acme.test");

        claims.GetProperty("preferred_username").GetString().ShouldBe("tom@acme.test");
        // Personal data goes to the ID token (the gateway reads it there), not to the tokens services receive.
        claims.TryGetProperty("name", out _).ShouldBeFalse();
        claims.TryGetProperty("email", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("tom@acme.test")]
    [InlineData("maria@acme.test")]
    [InlineData("ben@globex.test")]
    public async Task Access_token_carries_the_user_id_in_sub(string user)
    {
        // The services identify the caller by `sub`; without it every user is "anonymous" in the handlers.
        var claims = await keycloak.LoginAsync(user);

        var sub = claims.GetProperty("sub").GetString();
        sub.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(sub, out _).ShouldBeTrue("sub is the Keycloak user id");
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
    [InlineData("approver@demo.test", "demo", "manager")]
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
    [InlineData("approver@demo.test", "manager")]
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
            .Where(u => u.TryGetProperty("credentials", out _))
            .SelectMany(u => u.GetProperty("credentials").EnumerateArray())
            .Select(c => c.GetProperty("value").GetString()!).ToArray();
        passwords.ShouldNotBeEmpty();
        passwords.ShouldAllBe(v => v.StartsWith("${") && v.EndsWith('}'));

        var clients = realm.GetProperty("clients").EnumerateArray().ToArray();
        clients.ShouldNotContain(c => c.GetProperty("clientId").GetString() == KeycloakFixture.TestClient);
        clients.Count(c => c.TryGetProperty("directAccessGrantsEnabled", out var d) && d.GetBoolean()).ShouldBe(0);
        clients.Single(c => c.GetProperty("clientId").GetString() == "procure-gateway")
            .GetProperty("secret").GetString().ShouldBe("${KC_GATEWAY_SECRET}");
        clients.Single(c => c.GetProperty("clientId").GetString() == KeycloakFixture.InternalClient)
            .GetProperty("secret").GetString().ShouldBe("${KC_INTERNAL_SECRET}");
        clients.Single(c => c.GetProperty("clientId").GetString() == KeycloakFixture.AccessAdminClient)
            .GetProperty("secret").GetString().ShouldBe("${KC_ACCESS_ADMIN_SECRET}");
    }

    [Theory]
    [InlineData("tom@acme.test", DemoUsers.Tom)]
    [InlineData("maria@acme.test", DemoUsers.Maria)]
    [InlineData("dept@acme.test", DemoUsers.Dept)]
    [InlineData("thandi@acme.test", DemoUsers.Thandi)]
    [InlineData("admin@acme.test", DemoUsers.Admin)]
    [InlineData("ben@globex.test", DemoUsers.Ben)]
    [InlineData("approver@demo.test", DemoUsers.DemoApprover)]
    public async Task Demo_users_have_fixed_ids(string user, string id)
    {
        // Seeds in other services refer to these IDs, so the realm import must pin them.
        var claims = await keycloak.LoginAsync(user);

        claims.GetProperty("sub").GetString().ShouldBe(id);
    }

    [Fact]
    public async Task Internal_client_issues_client_credentials_token()
    {
        var claims = KeycloakFixture.Payload(await keycloak.InternalTokenAsync());

        claims.GetProperty("azp").GetString().ShouldBe(KeycloakFixture.InternalClient);
        Audiences(claims).ShouldContain("procure-api");
        claims.TryGetProperty("tenant", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Internal_client_cannot_log_a_user_in()
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{keycloak.BaseUrl}/realms/{KeycloakFixture.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = KeycloakFixture.InternalClient,
                ["client_secret"] = keycloak.InternalSecret,
                ["username"] = "tom@acme.test",
                ["password"] = keycloak.DemoPassword,
            }));

        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task Access_admin_service_account_can_create_a_user_and_add_it_to_an_organization()
    {
        // What the Access service does for a self-registered user (plan 02, task 4): join the `demo` organization.
        using var http = new HttpClient { BaseAddress = new Uri(keycloak.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await keycloak.AccessAdminTokenAsync());
        var admin = $"/admin/realms/{KeycloakFixture.Realm}";

        var users = await http.GetAsync($"{admin}/users?username=tom@acme.test&exact=true", Ct);
        users.StatusCode.ShouldBe(HttpStatusCode.OK);
        var organizationsResponse = await http.GetAsync($"{admin}/organizations?search=demo&exact=false", Ct);
        organizationsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await organizationsResponse.Content.ReadAsStringAsync(Ct));
        var organizations = await organizationsResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var demo = organizations.EnumerateArray().Single(o => o.GetProperty("alias").GetString() == "demo");

        var created = await http.PostAsJsonAsync($"{admin}/users", new { username = "newbie@demo.test", email = "newbie@demo.test", enabled = true }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var userId = created.Headers.Location!.Segments[^1];
        var added = await http.PostAsJsonAsync($"{admin}/organizations/{demo.GetProperty("id").GetString()}/members", userId, Ct);

        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("users")]
    [InlineData("organizations")]
    [InlineData("clients")]
    public async Task Internal_client_token_gets_403_from_the_Keycloak_admin_API(string resource)
    {
        // The internal client is for service-to-service calls and is in every service: it must hold no admin rights.
        using var http = new HttpClient { BaseAddress = new Uri(keycloak.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await keycloak.InternalTokenAsync());

        var response = await http.GetAsync($"/admin/realms/{KeycloakFixture.Realm}/{resource}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] BusinessRoles = ["requester", "manager", "department-head", "finance", "admin"];

    private static string[] Strings(JsonElement claims, string name) =>
        claims.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Audiences(JsonElement claims) =>
        claims.GetProperty("aud").ValueKind == JsonValueKind.Array ? Strings(claims, "aud") : [claims.GetProperty("aud").GetString()!];
}
