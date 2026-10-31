using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using ProcureAi.Contracts;
using StackExchange.Redis;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The tenant switch (plan 06 ruling 18, Task 14b): <c>POST /bff/switch-tenant</c> with <c>{ tenant, returnUrl }</c> and the XSRF header
/// checks the membership with Access, then gets a token for that one organization and replaces the session with a new one. A demo
/// session switches inside the POST (the demo client's password grant); a code-flow session gets a single-use challenge URL
/// (<c>/bff/switch-tenant/challenge?ticket=</c>) that starts the silent OIDC challenge with <c>organization:globex</c>. The POST answers
/// <c>200 { navigateTo }</c>; nothing changes on a plain GET. Real Keycloak (Thandi is in Acme and Globex) and Redis, stub Access.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class TenantSwitchTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Api = "/api/v1/requests";
    private const string Thandi = "thandi@acme.test";

    private GatewayHost _gateway = null!;
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _gateway = await GatewayHost.StartAsync(keycloak, redis, new()
        {
            ["DemoLogin:Enabled"] = "true",
            ["DemoLogin:ClientSecret"] = keycloak.DemoLoginSecret,
            ["DemoLogin:Password"] = keycloak.DemoPassword,
        });
        _gateway.Access.SetUserTenants(DemoUsers.Thandi, ("acme", "Acme Corp"), ("globex", "Globex"));
        _gateway.Access.SetUserTenants(DemoUsers.Tom, ("acme", "Acme Corp"));
        _gateway.Access.SetTenantName("acme", "Acme Corp");
        _gateway.Access.SetTenantName("globex", "Globex");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        await _gateway.DisposeAsync();
    }

    /// <summary>A signed-in browser: its client, its cookie jar, and (code flow) the Keycloak side holding the SSO session.</summary>
    private sealed record SignedIn(HttpClient Browser, CookieJarHandler Cookies, LoginDriver? Keycloak);

    private async Task<SignedIn> SignInAsync(string mode, string user = Thandi, string organization = "acme")
    {
        var (browser, cookies) = _gateway.NewBrowserWithCookies();
        _disposables.Add(browser);
        if (mode == "demo")
        {
            using var users = await browser.GetAsync("/bff/demo-login/users", Ct);
            var xsrf = Xsrf(users);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/demo-login")
            {
                Content = JsonContent.Create(new { user = user.Split('@')[0] }),
            };
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
            using var login = await browser.SendAsync(request, Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            return new SignedIn(browser, cookies, null);
        }

        var driver = new LoginDriver(keycloak);
        _disposables.Add(driver);
        await driver.LoginAsync(browser, user, organization: organization);
        return new SignedIn(browser, cookies, driver);
    }

    /// <summary>The switch POST like the app sends it: JSON body, the XSRF header (unless <paramref name="xsrf"/> is false).</summary>
    private static async Task<HttpResponseMessage> PostSwitchAsync(HttpClient browser, string tenant, string returnUrl = "/app/dashboard", bool xsrf = true)
    {
        using var user = await browser.GetAsync("/bff/user", Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/switch-tenant")
        {
            Content = JsonContent.Create(new { tenant, returnUrl }),
        };
        if (xsrf)
        {
            request.Headers.Add("X-XSRF-TOKEN", Xsrf(user));
        }

        return await browser.SendAsync(request, Ct);
    }

    private static async Task<string> NavigateToAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldBeNull("the switch POST never redirects");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("navigateTo").GetString()!;
    }

    /// <summary>
    /// Switches like the app does: the POST, then a full page load of <c>navigateTo</c>, followed to where the gateway sends the browser
    /// at the end. A code-flow session goes to Keycloak and back; the authorize request must ask silently for exactly that organization.
    /// Returns the final local URL the browser lands on.
    /// </summary>
    private static async Task<string> SwitchAsync(SignedIn session, string tenant, string returnUrl = "/app/dashboard")
    {
        using var post = await PostSwitchAsync(session.Browser, tenant, returnUrl);
        var navigateTo = await NavigateToAsync(post);
        if (session.Keycloak is null)
        {
            return navigateTo;
        }

        using var response = await session.Browser.GetAsync(navigateTo, Ct);
        LoginDriver.IsRedirect(response.StatusCode).ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        var authorize = response.Headers.Location!;
        if (!authorize.IsAbsoluteUri || !authorize.AbsolutePath.StartsWith("/realms/", StringComparison.Ordinal))
        {
            return authorize.OriginalString;
        }

        // The handler pushes the request to Keycloak first (PAR) when Keycloak offers it: then the browser URL holds only a request_uri.
        // Silence (prompt=none) and the one organization show in the outcome: no page below, and the new token's tenant.
        var query = QueryHelpers.ParseQuery(authorize.Query);
        if (!query.ContainsKey("request_uri"))
        {
            query["prompt"].ToString().ShouldBe("none");
            var scopes = query["scope"].ToString().Split(' ');
            scopes.ShouldContain($"organization:{tenant}");
            scopes.ShouldNotContain("organization", "Keycloak refuses the bare scope next to organization:<alias>");
        }
        var callback = await session.Keycloak.FollowSilentAsync(authorize);
        using var landed = await session.Browser.GetAsync(callback, Ct);
        LoginDriver.IsRedirect(landed.StatusCode).ShouldBeTrue();
        return landed.Headers.Location!.OriginalString;
    }

    private static string Xsrf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    private static async Task<JsonElement> UserAsync(HttpClient browser) => await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

    private async Task<string> ForwardedTenantAsync(HttpClient browser)
    {
        (await browser.GetAsync(Api, Ct)).EnsureSuccessStatusCode();
        var header = _gateway.Stub.Last.Header("Authorization")!;
        return new JsonWebTokenHandler().ReadJsonWebToken(header["Bearer ".Length..]).GetClaim("tenant").Value;
    }

    [Theory]
    [InlineData("code")]
    [InlineData("demo")]
    public async Task Switch_to_member_tenant_issues_token_for_that_tenant(string mode)
    {
        var session = await SignInAsync(mode);
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");

        (await SwitchAsync(session, "globex")).ShouldBe("/app/dashboard");

        var user = await UserAsync(session.Browser);
        user.GetProperty("userId").GetString().ShouldBe(DemoUsers.Thandi);
        user.GetProperty("tenantId").GetString().ShouldBe("globex");
        user.GetProperty("tenantName").GetString().ShouldBe("Globex");
        (await ForwardedTenantAsync(session.Browser)).ShouldBe("globex");

        // The new tokens keep the organization when they are refreshed on the server.
        _gateway.Clock.Advance(TimeSpan.FromHours(1));
        (await ForwardedTenantAsync(session.Browser)).ShouldBe("globex");

        // And back again.
        (await SwitchAsync(session, "acme")).ShouldBe("/app/dashboard");
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
        (await ForwardedTenantAsync(session.Browser)).ShouldBe("acme");
    }

    [Theory]
    [InlineData("code", "demo")]
    [InlineData("code", "nope")]
    [InlineData("demo", "demo")]
    [InlineData("demo", "nope")]
    [InlineData("demo", "GLOBEX")]
    [InlineData("demo", "globex acme")]
    [InlineData("demo", "")]
    public async Task Switch_to_non_member_tenant_is_403(string mode, string tenant)
    {
        var session = await SignInAsync(mode);
        var before = SessionKeys();

        using var response = await PostSwitchAsync(session.Browser, tenant);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Location.ShouldBeNull("no challenge for a tenant the user does not belong to");
        SessionKeys().ShouldBe(before);
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task Switch_is_refused_while_access_cannot_answer()
    {
        var session = await SignInAsync("demo");
        _gateway.Access.Down = true;
        try
        {
            using var response = await PostSwitchAsync(session.Browser, "globex");

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            response.Headers.Location.ShouldBeNull();
        }
        finally
        {
            _gateway.Access.Down = false;
        }

        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task Switch_is_refused_with_503_when_the_access_circuit_is_open()
    {
        // The Access client's resilience pipeline throws its own exceptions (open circuit, total timeout), not HttpRequestException.
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis, new()
        {
            ["DemoLogin:Enabled"] = "true",
            ["DemoLogin:ClientSecret"] = keycloak.DemoLoginSecret,
            ["DemoLogin:Password"] = keycloak.DemoPassword,
        }, configureServices: services => services.AddHttpClient(nameof(ProcureAi.Gateway.Bff.IUserTenantsClient))
            .AddHttpMessageHandler(() => new OpenCircuitHandler()));
        var (browser, _) = gateway.NewBrowserWithCookies();
        _disposables.Add(browser);
        using (var users = await browser.GetAsync("/bff/demo-login/users", Ct))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/bff/demo-login") { Content = JsonContent.Create(new { user = "thandi" }) })
        {
            request.Headers.Add("X-XSRF-TOKEN", Xsrf(users));
            (await browser.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var response = await PostSwitchAsync(browser, "globex");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.Location.ShouldBeNull("no challenge");
        (await UserAsync(browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    /// <summary>Stands in for the resilience pipeline with its circuit open: the Access call throws before it is sent.</summary>
    private sealed class OpenCircuitHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Polly.CircuitBreaker.BrokenCircuitException("The circuit is now open");
    }

    [Fact]
    public async Task Demo_switch_ends_the_old_keycloak_session()
    {
        // A demo switch is a new password grant (a new Keycloak session); the old one must end, or switches pile them up.
        var session = await SignInAsync("demo");
        var before = await KeycloakSessionsAsync(DemoUsers.Thandi);

        (await SwitchAsync(session, "globex")).ShouldBe("/app/dashboard");
        (await SwitchAsync(session, "acme")).ShouldBe("/app/dashboard");

        (await KeycloakSessionsAsync(DemoUsers.Thandi)).ShouldBe(before, "each switch ends the session it replaces");
    }

    private async Task<int> KeycloakSessionsAsync(string userId)
    {
        using var admin = await keycloak.AdminHttpAsync();
        var sessions = await admin.GetFromJsonAsync<JsonElement>($"/admin/realms/{KeycloakFixture.Realm}/users/{userId}/sessions", Ct);
        return sessions.GetArrayLength();
    }

    [Fact]
    public async Task Switch_needs_a_session()
    {
        var browser = _gateway.NewBrowser();
        _disposables.Add(browser);

        using var response = await browser.PostAsJsonAsync("/bff/switch-tenant", new { tenant = "globex", returnUrl = "/app/dashboard" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();

        using var challenge = await browser.GetAsync("/bff/switch-tenant/challenge?ticket=anything", Ct);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        challenge.Headers.Location.ShouldBeNull();
    }

    [Theory]
    [InlineData("code")]
    [InlineData("demo")]
    public async Task Old_session_is_revoked_after_switch(string mode)
    {
        var session = await SignInAsync(mode);
        var oldCookie = session.Cookies.CookieHeaderFor(_gateway.Address);
        var oldKeys = SessionKeys();
        oldKeys.Length.ShouldBe(1);

        (await SwitchAsync(session, "globex")).ShouldBe("/app/dashboard");

        // One session for this browser, under a new key; the old one is gone from Redis and from the user's index.
        var newKeys = SessionKeys();
        newKeys.Length.ShouldBe(1);
        newKeys.ShouldNotBe(oldKeys);
        UserIndex(DemoUsers.Thandi).Length.ShouldBe(1);
        UserIndex(DemoUsers.Thandi).Single().ShouldBe(newKeys.Single()[(_gateway.KeyPrefix + "session:").Length..]);

        // A copy of the old cookie opens nothing any more.
        var copy = _gateway.NewBrowser();
        _disposables.Add(copy);
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/bff/user");
        replay.Headers.Add("Cookie", oldCookie);
        (await copy.SendAsync(replay, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // "Log out everywhere" still reaches the new session.
        using var user = await session.Browser.GetAsync("/bff/user", Ct);
        using var revoke = new HttpRequestMessage(HttpMethod.Post, "/bff/logout-all");
        revoke.Headers.Add("X-XSRF-TOKEN", Xsrf(user));
        using var revoked = await session.Browser.SendAsync(revoke, Ct);
        (await revoked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revoked").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("code", "https://evil.test/app")]
    [InlineData("demo", "https://evil.test/app")]
    [InlineData("demo", "//evil.test/app")]
    [InlineData("demo", "/\\evil.test/app")]
    [InlineData("demo", "javascript:alert(1)")]
    public async Task Switch_returns_to_a_local_path_only(string mode, string returnUrl)
    {
        var session = await SignInAsync(mode);

        (await SwitchAsync(session, "globex", returnUrl)).ShouldBe("/");
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("globex");
    }

    [Fact]
    public async Task Keycloak_refusing_the_tenant_keeps_the_old_session()
    {
        // Access (here: a stub that is wrong) says Tom is in Globex, Keycloak knows better: the switch fails, nothing changes.
        _gateway.Access.SetUserTenants(DemoUsers.Tom, ("acme", "Acme Corp"), ("globex", "Globex"));
        var session = await SignInAsync("code", "tom@acme.test");
        var oldKeys = SessionKeys();

        // The silent request: Keycloak answers with an error instead of a code.
        using var post = await PostSwitchAsync(session.Browser, "globex");
        using var start = await session.Browser.GetAsync(await NavigateToAsync(post), Ct);
        var callback = await session.Keycloak!.FollowSilentAsync(start.Headers.Location!);
        using var silent = await session.Browser.GetAsync(callback, Ct);
        var final = silent.Headers.Location!;
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");

        // The gateway retries once at once, visibly (no prompt=none: for a Keycloak session that ended); signing in there still gives no
        // Globex token, so the callback refuses it and the browser goes back to where it started.
        final.IsAbsoluteUri.ShouldBeTrue("the visible retry goes to Keycloak");
        var (form, redirect) = await session.Keycloak.OpenLoginPageAsync(final);
        var second = redirect ?? await session.Keycloak.SubmitCredentialsAsync(form!, "tom@acme.test", keycloak.DemoPassword);
        using (var visible = await session.Browser.GetAsync(second, Ct))
        {
            final = visible.Headers.Location!;
        }

        final.OriginalString.ShouldBe("/app/dashboard");
        final.IsAbsoluteUri.ShouldBeFalse();
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
        SessionKeys().ShouldBe(oldKeys);
        (await ForwardedTenantAsync(session.Browser)).ShouldBe("acme");
    }

    [Theory]
    [InlineData("code")]
    [InlineData("demo")]
    public async Task Get_switch_tenant_changes_nothing(string mode)
    {
        // A link or an image on any same-site page is a GET: it must neither switch nor start a challenge.
        var session = await SignInAsync(mode);
        var before = SessionKeys();

        using var response = await session.Browser.GetAsync("/bff/switch-tenant?tenant=globex&returnUrl=/app/dashboard", Ct);

        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        response.Headers.Location.ShouldBeNull("no challenge, no redirect");
        SessionKeys().ShouldBe(before);
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Theory]
    [InlineData("code")]
    [InlineData("demo")]
    public async Task Switch_post_without_xsrf_is_rejected(string mode)
    {
        var session = await SignInAsync(mode);
        var before = SessionKeys();

        using var response = await PostSwitchAsync(session.Browser, "globex", xsrf: false);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("anti-forgery");
        response.Headers.Location.ShouldBeNull();
        SessionKeys().ShouldBe(before);
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Theory]
    [InlineData("code")]
    [InlineData("demo")]
    public async Task Switch_post_for_member_returns_navigate_url(string mode)
    {
        var session = await SignInAsync(mode);

        using var response = await PostSwitchAsync(session.Browser, "globex", "/app/budgets");
        var navigateTo = await NavigateToAsync(response);

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        if (mode == "demo")
        {
            // Switched inside the POST; the app just goes where it asked to.
            navigateTo.ShouldBe("/app/budgets");
            (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("globex");
        }
        else
        {
            // Nothing has happened yet: the URL is the gateway's own, single-use start of the challenge.
            navigateTo.ShouldStartWith("/bff/switch-tenant/challenge?ticket=");
            navigateTo.ShouldNotContain("globex");
            (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
        }

        // The current company: nothing to do, the app goes to the return URL.
        using var same = await PostSwitchAsync(session.Browser, mode == "demo" ? "globex" : "acme", "/app/requests");
        (await NavigateToAsync(same)).ShouldBe("/app/requests");
    }

    [Fact]
    public async Task Challenge_ticket_is_single_use()
    {
        var session = await SignInAsync("code");
        using var post = await PostSwitchAsync(session.Browser, "globex");
        var navigateTo = await NavigateToAsync(post);

        using (var first = await session.Browser.GetAsync(navigateTo, Ct))
        {
            LoginDriver.IsRedirect(first.StatusCode).ShouldBeTrue();
            first.Headers.Location!.AbsolutePath.ShouldStartWith("/realms/", Case.Sensitive, "the first use starts the challenge");
        }

        using var second = await session.Browser.GetAsync(navigateTo, Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        second.Headers.Location.ShouldBeNull("a used ticket starts nothing");

        using var madeUp = await session.Browser.GetAsync("/bff/switch-tenant/challenge?ticket=not-a-ticket", Ct);
        madeUp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missing = await session.Browser.GetAsync("/bff/switch-tenant/challenge", Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Challenge_ticket_of_another_session_is_rejected()
    {
        var thandi = await SignInAsync("code");
        using var post = await PostSwitchAsync(thandi.Browser, "globex");
        var navigateTo = await NavigateToAsync(post);

        // Another user, and the same user in another browser: neither may start the challenge with it.
        var tom = await SignInAsync("code", "tom@acme.test");
        using (var other = await tom.Browser.GetAsync(navigateTo, Ct))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            other.Headers.Location.ShouldBeNull();
        }

        var thandiElsewhere = await SignInAsync("demo");
        using (var elsewhere = await thandiElsewhere.Browser.GetAsync(navigateTo, Ct))
        {
            elsewhere.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            elsewhere.Headers.Location.ShouldBeNull();
        }

        (await UserAsync(tom.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
        (await UserAsync(thandiElsewhere.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");

        // Refused elsewhere, the ticket still works for the session it was made for.
        using var own = await thandi.Browser.GetAsync(navigateTo, Ct);
        LoginDriver.IsRedirect(own.StatusCode).ShouldBeTrue();
        own.Headers.Location!.AbsolutePath.ShouldStartWith("/realms/");
    }

    [Fact]
    public async Task Expired_challenge_ticket_is_rejected()
    {
        var session = await SignInAsync("code");
        using var post = await PostSwitchAsync(session.Browser, "globex");
        var navigateTo = await NavigateToAsync(post);

        _gateway.Clock.Advance(TimeSpan.FromSeconds(61));
        using var response = await session.Browser.GetAsync(navigateTo, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task User_lists_the_tenants_from_access()
    {
        var thandi = await SignInAsync("demo");
        var tom = await SignInAsync("demo", "tom@acme.test");

        var thandiUser = await UserAsync(thandi.Browser);
        thandiUser.GetProperty("tenantName").GetString().ShouldBe("Acme Corp");
        Tenants(thandiUser).ShouldBe([("acme", "Acme Corp"), ("globex", "Globex")]);
        Tenants(await UserAsync(tom.Browser)).ShouldBe([("acme", "Acme Corp")]);

        // Access down: who-am-I still answers, with the current tenant only (so the app offers no switch).
        _gateway.Access.Down = true;
        try
        {
            Tenants(await UserAsync(thandi.Browser)).Select(t => t.Id).ShouldBe(["acme"]);
        }
        finally
        {
            _gateway.Access.Down = false;
        }
    }

    [Fact]
    public async Task Code_flow_login_of_multi_org_user_asks_which_company_and_gets_exactly_that_one()
    {
        // Pre-flight ruling P5: every login yields one tenant. Repeated, because a wrong realm setup gave a tenant only sometimes.
        for (var i = 0; i < 6; i++)
        {
            var organization = i % 2 == 0 ? "acme" : "globex";
            var session = await SignInAsync("code", Thandi, organization);

            session.Keycloak!.OfferedOrganizations.ShouldBe(["acme", "globex"], ignoreOrder: true);
            (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe(organization, $"login {i}");
            (await ForwardedTenantAsync(session.Browser)).ShouldBe(organization, $"login {i}");
        }
    }

    [Fact]
    public async Task Code_flow_login_of_single_org_user_needs_no_choice()
    {
        var session = await SignInAsync("code", "tom@acme.test");

        session.Keycloak!.OfferedOrganizations.ShouldBeEmpty();
        (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task Demo_login_of_multi_org_user_gets_its_configured_tenant_every_time()
    {
        // Thandi's demo entry names acme; with her in two organizations the token must say acme every time (Task 13 saw "sometimes").
        for (var i = 0; i < 15; i++)
        {
            var session = await SignInAsync("demo");
            (await UserAsync(session.Browser)).GetProperty("tenantId").GetString().ShouldBe("acme", $"login {i}");
        }
    }

    private static (string Id, string Name)[] Tenants(JsonElement user) =>
        [.. user.GetProperty("tenants").EnumerateArray().Select(t => (t.GetProperty("id").GetString()!, t.GetProperty("name").GetString()!))];

    private string[] SessionKeys()
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString + ",allowAdmin=true");
        var server = connection.GetServer(connection.GetEndPoints().First());
        return [.. server.Keys(pattern: $"{_gateway.KeyPrefix}session:*").Select(k => k.ToString()).Order(StringComparer.Ordinal)];
    }

    private string[] UserIndex(string subject)
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString);
        return [.. connection.GetDatabase().SetMembers($"{_gateway.KeyPrefix}user:{subject}").Select(v => v.ToString())];
    }
}
