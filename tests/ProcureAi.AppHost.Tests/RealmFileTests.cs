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
    private const string ArgoCdTunnelUrl = "${KC_ARGOCD_TUNNEL_URL:http://127.0.0.1:8080}";

    private static readonly JsonElement Realm = JsonDocument.Parse(File.ReadAllText("procure-realm.json")).RootElement;
    private static readonly JsonElement OpsRealm = JsonDocument.Parse(File.ReadAllText("ops-realm.json")).RootElement;

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
    public void Ops_realm_has_argocd_client_with_pkce_and_a_groups_claim()
    {
        // Plan 08 Task 12c, moved to realm ops by fix wave ruling R6c: Argo CD signs people in with Keycloak (public client, code flow + PKCE,
        // no secret: Argo CD's documented Keycloak setup), and maps the group argocd-admins to role:admin from a plain `groups` claim
        // (GitOps repo argocd/values.yaml).
        var argocd = OpsRealm.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "argocd");
        argocd.GetProperty("publicClient").GetBoolean().ShouldBeTrue();
        argocd.TryGetProperty("secret", out _).ShouldBeFalse();
        argocd.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeTrue();
        argocd.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
        argocd.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().ShouldBe("S256");
        // Exactly Argo CD's two callbacks (server: /auth/callback, UI with PKCE: /pkce/verify) under the environment's base and under the
        // tunnel's port-forward (plan 08c ruling R3: Argo CD's additionalUrls, so SSO still finishes through the tunnel); no wildcard.
        Strings(argocd, "redirectUris").ShouldBe([$"{ArgoCdBaseUrl}/auth/callback", $"{ArgoCdBaseUrl}/pkce/verify",
            $"{ArgoCdTunnelUrl}/auth/callback", $"{ArgoCdTunnelUrl}/pkce/verify"]);
        Strings(argocd, "webOrigins").ShouldBe([ArgoCdBaseUrl, ArgoCdTunnelUrl]);
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
    public void Ops_realm_has_the_argocd_gate_client_for_the_public_host()
    {
        // Plan 08c ruling R9: oauth2-proxy (Traefik forwardAuth) in front of the public Argo CD host lets in members of argocd-admins only.
        // A public client with PKCE like argocd (Keycloak skips client authentication for a public client; the gate's protection is the
        // user's password, one-time code and group), code flow only, one callback under the Argo CD base; the groups claim the gate checks.
        var gate = OpsRealm.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "argocd-gate");
        gate.GetProperty("publicClient").GetBoolean().ShouldBeTrue();
        gate.TryGetProperty("secret", out _).ShouldBeFalse();
        gate.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeTrue();
        gate.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        gate.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
        gate.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
        gate.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().ShouldBe("S256");
        Strings(gate, "redirectUris").ShouldBe([$"{ArgoCdBaseUrl}/oauth2/callback"]);
        Strings(gate, "webOrigins").ShouldBeEmpty();
        Strings(gate, "defaultClientScopes").ShouldBeEmpty();
        Strings(gate, "optionalClientScopes").ShouldBeEmpty();
        gate.GetProperty("description").GetString()!.Length.ShouldBeLessThanOrEqualTo(255);

        var groups = gate.GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-group-membership-mapper");
        groups.GetProperty("config").GetProperty("claim.name").GetString().ShouldBe("groups");
        groups.GetProperty("config").GetProperty("full.path").GetString().ShouldBe("false");
        groups.GetProperty("config").GetProperty("id.token.claim").GetString().ShouldBe("true");
        // oauth2-proxy names the session by the e-mail claim of the ID token.
        gate.GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "email").GetProperty("config").GetProperty("id.token.claim").GetString().ShouldBe("true");
    }

    [Fact]
    public void Ops_realm_has_the_argocd_admins_group_and_no_users()
    {
        OpsRealm.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).ShouldBe(["argocd-admins"]);
        // Dale creates his own user (bootstrap/add-argocd-admin.ps1 in the GitOps repo); the file holds no user, so no demo user can exist
        // in the realm Argo CD trusts.
        OpsRealm.TryGetProperty("users", out _).ShouldBeFalse();
    }

    [Fact]
    public void Ops_realm_requires_otp_locks_out_guessers_and_allows_no_self_service()
    {
        // Ruling R6c: unlike the demo realm (shared users, no lockout), every ops user is a person's own: a temporary lockout after 5 failed
        // passwords (1 minute more per further 5, at most 15 minutes, never permanent), and an authenticator app for everyone (Configure
        // OTP is a default required action of every new user; the browser flow then asks for the code at every sign-in).
        OpsRealm.GetProperty("realm").GetString().ShouldBe("ops");
        OpsRealm.GetProperty("bruteForceProtected").GetBoolean().ShouldBeTrue();
        OpsRealm.GetProperty("permanentLockout").GetBoolean().ShouldBeFalse();
        OpsRealm.GetProperty("failureFactor").GetInt32().ShouldBe(5);
        OpsRealm.GetProperty("maxFailureWaitSeconds").GetInt32().ShouldBe(900);
        var totp = OpsRealm.GetProperty("requiredActions").EnumerateArray().Single(a => a.GetProperty("alias").GetString() == "CONFIGURE_TOTP");
        totp.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        totp.GetProperty("defaultAction").GetBoolean().ShouldBeTrue();
        OpsRealm.GetProperty("otpPolicyType").GetString().ShouldBe("totp");
        OpsRealm.GetProperty("otpPolicyDigits").GetInt32().ShouldBe(6);
        OpsRealm.GetProperty("otpPolicyPeriod").GetInt32().ShouldBe(30);
        OpsRealm.GetProperty("registrationAllowed").GetBoolean().ShouldBeFalse();
        OpsRealm.GetProperty("resetPasswordAllowed").GetBoolean().ShouldBeFalse();
        OpsRealm.GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("clientId").GetString()).ShouldBe(["argocd", "argocd-gate"]);
    }

    [Fact]
    public void Ops_realm_sign_in_always_needs_the_one_time_code()
    {
        // Security review of R6c: the built-in browser flow asks for OTP only when the user has one ("Condition - User Configured"), so a
        // user whose authenticator was deleted, or who was created without the Configure OTP action, would sign in with a password alone.
        // Realm ops binds its own flows: the OTP Form is REQUIRED (a user without an authenticator must set one up before the sign-in
        // completes), and password grants need the code as well.
        OpsRealm.GetProperty("browserFlow").GetString().ShouldBe("ops browser");
        OpsRealm.GetProperty("directGrantFlow").GetString().ShouldBe("ops direct grant");
        var flows = OpsRealm.GetProperty("authenticationFlows").EnumerateArray().ToDictionary(f => f.GetProperty("alias").GetString()!);

        string[] Steps(string alias) => flows[alias].GetProperty("authenticationExecutions").EnumerateArray()
            .Select(e => $"{(e.TryGetProperty("authenticator", out var a) ? a.GetString() : "flow:" + e.GetProperty("flowAlias").GetString())}={e.GetProperty("requirement").GetString()}")
            .ToArray();

        Steps("ops browser").ShouldBe(["auth-cookie=ALTERNATIVE", "flow:ops browser forms=ALTERNATIVE"]);
        Steps("ops browser forms").ShouldBe(["auth-username-password-form=REQUIRED", "auth-otp-form=REQUIRED"]);
        Steps("ops direct grant").ShouldBe(
            ["direct-grant-validate-username=REQUIRED", "direct-grant-validate-password=REQUIRED", "direct-grant-validate-otp=REQUIRED"]);
        // No conditional step anywhere: nothing can make the code optional.
        foreach (var flow in flows.Values)
        {
            flow.GetProperty("authenticationExecutions").EnumerateArray()
                .Select(e => e.TryGetProperty("authenticator", out var a) ? a.GetString() : null)
                .ShouldNotContain(x => x != null && x.StartsWith("conditional-", StringComparison.Ordinal), flow.GetProperty("alias").GetString());
            flow.GetProperty("authenticationExecutions").EnumerateArray().Select(e => e.GetProperty("requirement").GetString())
                .ShouldNotContain("CONDITIONAL");
        }
    }

    [Fact]
    public void Demo_realm_allows_no_password_grant_through_admin_cli()
    {
        // Re-review m3: the demo realm has no lockout (shared users), so its built-in public client admin-cli must not take password grants
        // at the token endpoint. Listed in the file with Keycloak's defaults except directAccessGrantsEnabled.
        var adminCli = Client("admin-cli");
        adminCli.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        adminCli.GetProperty("publicClient").GetBoolean().ShouldBeTrue();
        adminCli.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeFalse();
        adminCli.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Ops_realm_signs_in_by_private_user_name_only()
    {
        // Re-review m4: with the e-mail address as sign-in name, a stranger who knows Dale's (public commit) address could keep his user
        // locked out. Realm ops signs in by a user name published nowhere.
        OpsRealm.GetProperty("registrationEmailAsUsername").GetBoolean().ShouldBeFalse();
        OpsRealm.GetProperty("loginWithEmailAllowed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Demo_realm_has_no_argocd_client_or_admins_group()
    {
        // Ruling R6c: the demo realm (public, shared users, no lockout) is not Argo CD's directory any more.
        Realm.GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("clientId").GetString()).ShouldNotContain("argocd");
        // An empty list, not a missing key: keycloak-config-cli only removes groups it created when the file lists groups at all.
        Realm.GetProperty("groups").GetArrayLength().ShouldBe(0);
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
