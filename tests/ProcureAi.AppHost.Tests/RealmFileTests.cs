using System.Text.Json;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Fast checks (no containers) of the committed realm file, which the AppHost bind-mounts and the deployed Keycloak image bakes in
/// (plan 07 ruling 11). Where dev and deployed differ, the realm holds a placeholder with the local value as its default.
/// </summary>
public sealed class RealmFileTests
{
    private const string AppBaseUrl = "${KC_APP_BASE_URL:https://localhost}";
    private const string DashboardBaseUrl = "${KC_DASHBOARD_BASE_URL:https://localhost}";

    private static readonly JsonElement Realm = JsonDocument.Parse(File.ReadAllText("procure-realm.json")).RootElement;

    [Fact]
    public void Realm_has_dashboard_client_with_placeholder_secret()
    {
        var dashboard = Client("aspire-dashboard");

        // Confidential, code flow only: the standalone Aspire dashboard signs users in with OIDC (CLAUDE.md "Observability").
        dashboard.GetProperty("publicClient").GetBoolean().ShouldBeFalse();
        dashboard.GetProperty("secret").GetString().ShouldBe("${KC_DASHBOARD_SECRET}");
        dashboard.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeTrue();
        dashboard.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        dashboard.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
        dashboard.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
        dashboard.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().ShouldBe("S256");
        // The Aspire dashboard (13.6) fixes its OIDC callback path to "/" in code (found in plan 07 Task 7), so its redirect URI is <base>/
        Strings(dashboard, "redirectUris").ShouldBe([$"{DashboardBaseUrl}/"]);
        // It always asks for "openid profile" (configuration cannot drop profile), so the realm has a `profile` scope, optional here.
        Strings(dashboard, "optionalClientScopes").ShouldBe(["profile"]);
        dashboard.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString()
            .ShouldBe($"{DashboardBaseUrl}/signout-callback-oidc");
        // No procure-api audience: its tokens are for the dashboard only.
        Strings(dashboard, "defaultClientScopes").ShouldNotContain("procure-api");

        // The client role the dashboard requires, emitted as a flat claim it can check (Dashboard:Frontend:OpenIdConnect:RequiredClaimType).
        Realm.GetProperty("roles").GetProperty("client").GetProperty("aspire-dashboard").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ShouldBe(["dashboard-viewer"]);
        var mapper = dashboard.GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-usermodel-client-role-mapper");
        mapper.GetProperty("config").GetProperty("usermodel.clientRoleMapping.clientId").GetString().ShouldBe("aspire-dashboard");
        mapper.GetProperty("config").GetProperty("claim.name").GetString().ShouldBe("dashboard_roles");
        mapper.GetProperty("config").GetProperty("id.token.claim").GetString().ShouldBe("true");
        mapper.GetProperty("config").GetProperty("multivalued").GetString().ShouldBe("true");

        // Only the admin demo user may open the dashboard.
        var viewers = Realm.GetProperty("users").EnumerateArray()
            .Where(u => u.TryGetProperty("clientRoles", out var roles)
                && roles.TryGetProperty("aspire-dashboard", out var dashboardRoles)
                && dashboardRoles.EnumerateArray().Any(r => r.GetString() == "dashboard-viewer"))
            .Select(u => u.GetProperty("username").GetString());
        viewers.ShouldBe(["admin@acme.test"]);
    }

    [Fact]
    public void Realm_profile_scope_exists_and_maps_nothing()
    {
        // Keycloak refuses a request for a scope the realm does not define ("Invalid scopes: openid profile"). The realm's own clientScopes
        // list replaces Keycloak's built-in ones, so `profile` is defined here, without mappers: every client keeps exactly the claims its
        // own mappers (or procure-api) give, and tokens only gain "profile" in their scope claim.
        var profile = Realm.GetProperty("clientScopes").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "profile");
        profile.GetProperty("protocol").GetString().ShouldBe("openid-connect");
        profile.TryGetProperty("protocolMappers", out var mappers).ShouldBeFalse($"profile has mappers: {mappers}");
    }

    [Fact]
    public void Realm_gateway_redirects_use_app_base_url_placeholder()
    {
        foreach (var clientId in new[] { "procure-gateway", "procure-swagger" })
        {
            var client = Client(clientId);
            Strings(client, "redirectUris").ShouldContain($"{AppBaseUrl}/*", clientId);
            Strings(client, "webOrigins").ShouldContain(AppBaseUrl, clientId);
            // Every redirect is the placeholder or a loopback address (the AppHost's random gateway port); nothing else.
            Strings(client, "redirectUris").ShouldAllBe(
                u => u == $"{AppBaseUrl}/*" || u == "https://localhost:*" || u == "http://localhost:*", clientId);
        }

        var logout = Client("procure-gateway").GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString()!;
        logout.Split("##").ShouldContain($"{AppBaseUrl}/*");
    }

    private static JsonElement Client(string clientId) =>
        Realm.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == clientId);

    private static string[] Strings(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
}
