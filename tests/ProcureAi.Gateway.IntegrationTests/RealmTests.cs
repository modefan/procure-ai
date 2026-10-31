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
    public async Task Access_token_carries_the_username_and_name_but_no_email()
    {
        var claims = await keycloak.LoginAsync("tom@acme.test");

        claims.GetProperty("preferred_username").GetString().ShouldBe("tom@acme.test");
        // `name` (the full name) is in the access token on purpose (plan 04 Task 4a ruling): presence and the online list show display
        // names. Services must never log this claim. The email stays out: it goes to the ID token only (the gateway reads it there).
        claims.GetProperty("name").GetString().ShouldBe("Tom Mokoena");
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
    [InlineData("acme")]
    [InlineData("globex")]
    public async Task Thandi_in_two_organizations_gets_exactly_the_one_she_asks_for(string organization)
    {
        // Repeated: with the tenant mapper in procure-api, Keycloak picked the organization scope in a varying order and the claim
        // came only sometimes (plan 06 Task 13). One string, never a list, every time.
        for (var i = 0; i < 20; i++)
        {
            var claims = await keycloak.LoginAsync("thandi@acme.test", $"openid organization:{organization}");

            claims.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.String, $"login {i}");
            claims.GetProperty("tenant").GetString().ShouldBe(organization, $"login {i}");
            Strings(claims, "roles").ShouldContain("finance");
        }
    }

    [Fact]
    public async Task Multi_org_user_who_names_no_organization_gets_no_tenant_rather_than_any()
    {
        // No silent pick: without a choice (code flow: Keycloak's picker; demo login: its configured tenant) there is no tenant.
        for (var i = 0; i < 10; i++)
        {
            (await keycloak.LoginAsync("thandi@acme.test")).TryGetProperty("tenant", out _).ShouldBeFalse($"login {i}");
            (await keycloak.LoginAsync("thandi@acme.test", "openid organization")).TryGetProperty("tenant", out _).ShouldBeFalse($"login {i}");
        }
    }

    [Fact]
    public async Task Single_org_user_gets_the_tenant_every_time_with_or_without_the_organization_scope()
    {
        for (var i = 0; i < 10; i++)
        {
            (await keycloak.LoginAsync("tom@acme.test")).GetProperty("tenant").GetString().ShouldBe("acme", $"login {i}");
            (await keycloak.LoginAsync("tom@acme.test", "openid organization")).GetProperty("tenant").GetString().ShouldBe("acme", $"login {i}");
            (await keycloak.LoginAsync("tom@acme.test", "openid organization:acme")).GetProperty("tenant").GetString().ShouldBe("acme", $"login {i}");
        }
    }

    [Fact]
    public async Task User_cannot_ask_for_an_organization_they_are_not_in()
    {
        var claims = await keycloak.LoginAsync("tom@acme.test", "openid organization:globex");

        claims.TryGetProperty("tenant", out var tenant).ShouldBe(false, $"Tom got tenant {tenant}");
    }

    [Fact]
    public async Task Only_the_organization_scope_maps_organizations()
    {
        // Keycloak treats every client scope with an organization mapper as an organization scope and takes the first it meets in
        // the requested scopes. With two (procure-api and organization) the tenant depended on their order. Keep exactly one.
        var realm = JsonDocument.Parse(await File.ReadAllTextAsync("procure-realm.json")).RootElement;

        var mapping = realm.GetProperty("clientScopes").EnumerateArray()
            .Where(s => s.TryGetProperty("protocolMappers", out var mappers)
                        && mappers.EnumerateArray().Any(m => m.GetProperty("protocolMapper").GetString() == "oidc-organization-membership-mapper"))
            .ToArray();

        mapping.Select(s => s.GetProperty("name").GetString()).ShouldBe(["organization"]);
        var mapper = mapping.Single().GetProperty("protocolMappers").EnumerateArray().Single();
        mapper.GetProperty("config").GetProperty("claim.name").GetString().ShouldBe("tenant");
        mapper.GetProperty("config").GetProperty("multivalued").GetString().ShouldBe("false");
        mapper.GetProperty("config").GetProperty("access.token.claim").GetString().ShouldBe("true");
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
    public async Task Committed_realm_file_has_only_placeholder_credentials_and_only_the_demo_login_client_has_direct_access()
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
        // The only password-grant client is the gateway's demo login (plan 06 ruling 17): confidential, no browser flow.
        var direct = clients.Where(c => c.TryGetProperty("directAccessGrantsEnabled", out var d) && d.GetBoolean()).ToArray();
        direct.Select(c => c.GetProperty("clientId").GetString()).ShouldBe([KeycloakFixture.DemoLoginClient]);
        var demo = direct.Single();
        demo.GetProperty("publicClient").GetBoolean().ShouldBeFalse();
        demo.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeFalse();
        demo.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
        demo.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
        demo.GetProperty("secret").GetString().ShouldBe("${KC_DEMO_LOGIN_SECRET}");
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

    [Theory]
    [InlineData("tom@acme.test", "acme")]
    [InlineData("ben@globex.test", "globex")]
    public async Task Demo_login_client_accepts_the_organization_scope_and_issues_that_tenant(string user, string tenant)
    {
        // What the gateway's demo login asks for (pre-flight ruling P5): scope organization:<tenant>, so one tenant per login.
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{keycloak.BaseUrl}/realms/{KeycloakFixture.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = KeycloakFixture.DemoLoginClient,
                ["client_secret"] = keycloak.DemoLoginSecret,
                ["username"] = user,
                ["password"] = keycloak.DemoPassword,
                ["scope"] = $"openid organization:{tenant}",
            }), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var tokens = JsonDocument.Parse(body).RootElement;

        var claims = KeycloakFixture.Payload(tokens.GetProperty("access_token").GetString()!);

        claims.GetProperty("tenant").GetString().ShouldBe(tenant);
        claims.GetProperty("azp").GetString().ShouldBe(KeycloakFixture.DemoLoginClient);
        Audiences(claims).ShouldContain("procure-api");
        tokens.TryGetProperty("id_token", out _).ShouldBeTrue();
        tokens.TryGetProperty("refresh_token", out _).ShouldBeTrue();
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
