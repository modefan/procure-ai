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
    private const string ArgoCdBaseUrl = "${KC_ARGOCD_BASE_URL:http://127.0.0.1:8080}";

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
    public void Realm_clients_name_only_scopes_the_realm_has()
    {
        // Keycloak's own import skips a client scope the realm does not define, but keycloak-config-cli (the keycloak-config Job, plan 08
        // ruling 7) fails on it (NullPointerException in addDefaultClientScopes; `email` and `roles` were listed this way). The realm's
        // clientScopes replace Keycloak's built-in ones; only offline_access is still created by Keycloak itself (with its realm role).
        var defined = Realm.GetProperty("clientScopes").EnumerateArray().Select(s => s.GetProperty("name").GetString()!)
            .Append("offline_access").ToHashSet();
        foreach (var client in Realm.GetProperty("clients").EnumerateArray())
        {
            var clientId = client.GetProperty("clientId").GetString();
            foreach (var list in new[] { "defaultClientScopes", "optionalClientScopes" })
            {
                if (client.TryGetProperty(list, out _))
                    Strings(client, list).Where(s => !defined.Contains(s)).ShouldBeEmpty($"{clientId} {list}");
            }
        }
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

    [Fact]
    public void Realm_has_argocd_client_with_pkce_and_a_groups_claim()
    {
        // Plan 08 Task 12c: Argo CD signs people in with Keycloak (public client, code flow + PKCE, no secret: Argo CD's documented Keycloak
        // setup), and maps the group argocd-admins to role:admin from a plain `groups` claim (GitOps repo argocd/values.yaml).
        var argocd = Client("argocd");
        argocd.GetProperty("publicClient").GetBoolean().ShouldBeTrue();
        argocd.TryGetProperty("secret", out _).ShouldBeFalse();
        argocd.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeTrue();
        argocd.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().ShouldBe("S256");
        // Exactly Argo CD's two callbacks (server: /auth/callback, UI with PKCE: /pkce/verify) under the environment's base; no wildcard.
        Strings(argocd, "redirectUris").ShouldBe([$"{ArgoCdBaseUrl}/auth/callback", $"{ArgoCdBaseUrl}/pkce/verify"]);
        Strings(argocd, "webOrigins").ShouldBe([ArgoCdBaseUrl]);
        // Argo CD asks for "openid profile" only (no email or groups scope in this realm); the claims come from the client's own mappers.
        Strings(argocd, "defaultClientScopes").ShouldBeEmpty();
        Strings(argocd, "optionalClientScopes").ShouldBe(["profile"]);

        var groups = argocd.GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-group-membership-mapper");
        groups.GetProperty("config").GetProperty("claim.name").GetString().ShouldBe("groups");
        // Plain names ("argocd-admins", not "/argocd-admins"): the RBAC g-line matches the claim exactly.
        groups.GetProperty("config").GetProperty("full.path").GetString().ShouldBe("false");
        groups.GetProperty("config").GetProperty("id.token.claim").GetString().ShouldBe("true");
    }

    [Fact]
    public void Realm_has_the_argocd_admins_group_and_no_demo_user_is_in_it()
    {
        Realm.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).ShouldContain("argocd-admins");
        // Dale adds his own user by hand (CLAUDE.md "Hosting plan"); the public demo users never get Argo CD rights.
        foreach (var user in Realm.GetProperty("users").EnumerateArray())
        {
            if (user.TryGetProperty("groups", out var userGroups))
                userGroups.EnumerateArray().Select(g => g.GetString()!.TrimStart('/'))
                    .ShouldNotContain("argocd-admins", user.GetProperty("username").GetString());
        }
    }

    [Fact]
    public void Realm_does_not_allow_self_registration()
    {
        // Plan 08 Task 12c: nobody can create an account on the public demo's Keycloak (the login page shows no "Register"). The demo users
        // are the realm's own; the gateway's registration flow stays in the code and its tests turn registration on in their own realm copy.
        Realm.GetProperty("registrationAllowed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Realm_has_no_per_user_lockout()
    {
        // Plan 08 fix wave ruling R6b (replaces R6): Keycloak's brute-force detection locks a user after failed passwords, and the demo users
        // are shared and their names public, so a stranger could keep every "log in as" button failing with a few wrong passwords a minute.
        // Off, explicitly: keycloak-config-cli writes only the keys the file has, so a missing key would leave a running realm locked-on.
        // Password guessing is slowed per client address at Traefik instead (procure-platform keycloak.ingress.rateLimit).
        Realm.GetProperty("bruteForceProtected").GetBoolean().ShouldBeFalse();
        Realm.GetProperty("permanentLockout").GetBoolean().ShouldBeFalse();
        // The quick-login lock (two failures within a moment) stays off too, should detection ever be turned on again.
        Realm.GetProperty("quickLoginCheckMilliSeconds").GetInt64().ShouldBe(1);
    }

    private static JsonElement Client(string clientId) =>
        Realm.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == clientId);

    private static string[] Strings(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
}
