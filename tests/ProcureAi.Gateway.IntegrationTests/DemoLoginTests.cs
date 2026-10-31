using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using StackExchange.Redis;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// One-click demo login (plan 06 ruling 17): the gateway signs a listed demo user in with the password grant on the separate
/// <c>procure-demo-login</c> client and creates the same session as the code flow. Real Keycloak and Redis.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class DemoLoginTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Api = "/api/v1/requests";

    private GatewayHost _gateway = null!;
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the AppHost switches on; the user list itself comes from the gateway's appsettings.json.</summary>
    private Dictionary<string, string?> Enabled() => new()
    {
        ["DemoLogin:Enabled"] = "true",
        ["DemoLogin:ClientSecret"] = keycloak.DemoLoginSecret,
        ["DemoLogin:Password"] = keycloak.DemoPassword,
    };

    public async ValueTask InitializeAsync() => _gateway = await GatewayHost.StartAsync(keycloak, redis, Enabled());

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        await _gateway.DisposeAsync();
    }

    private HttpClient Browser(GatewayHost? gateway = null)
    {
        var browser = (gateway ?? _gateway).NewBrowser();
        _disposables.Add(browser);
        return browser;
    }

    /// <summary>What the welcome page does: list the demo users, which also hands out the XSRF token as a cookie.</summary>
    private static async Task<(JsonElement Users, string Xsrf)> UsersAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/demo-login/users", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        var xsrf = Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct), xsrf);
    }

    private static HttpRequestMessage Post(string url, object? body, string? xsrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> DemoLoginAsync(HttpClient browser, string user)
    {
        var (_, xsrf) = await UsersAsync(browser);
        return await browser.SendAsync(Post("/bff/demo-login", new { user }, xsrf), Ct);
    }

    private static async Task<string> SessionXsrfAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/user", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    private static JsonWebToken Bearer(SeenRequest seen)
    {
        var header = seen.Header("Authorization");
        header.ShouldNotBeNull();
        return new JsonWebTokenHandler().ReadJsonWebToken(header["Bearer ".Length..]);
    }

    [Fact]
    public async Task Users_lists_the_configured_demo_roles_without_usernames()
    {
        var (users, _) = await UsersAsync(Browser());

        users.EnumerateArray().Select(u => u.GetProperty("key").GetString()).ShouldBe(["tom", "maria", "dept", "thandi", "admin", "ben"]);
        users.EnumerateArray().Select(u => u.GetProperty("label").GetString()).ShouldBe(
            ["Requester (Tom)", "Manager (Maria)", "Department head", "Finance (Thandi)", "Admin", "Globex manager (Ben)"]);
        var tom = users.EnumerateArray().First();
        tom.GetProperty("tenant").GetString().ShouldBe("Acme Corp");
        // The browser never learns a username or password: it posts a key only.
        users.GetRawText().ShouldNotContain("@");
        users.GetRawText().ShouldNotContain(keycloak.DemoPassword);
    }

    [Fact]
    public async Task Demo_login_creates_session_like_code_flow()
    {
        var browser = Browser();

        using var response = await DemoLoginAsync(browser, "tom");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var session = setCookies.Single(c => c.StartsWith("__Host-procure=", StringComparison.Ordinal)).ToLowerInvariant();
        session.ShouldContain("httponly");
        session.ShouldContain("secure");
        session.ShouldContain("samesite=strict");
        response.Headers.GetValues("Clear-Site-Data").Single().ShouldBe("\"cache\"");
        SessionKeys().Length.ShouldBe(1, "the tokens are in the Redis ticket store, like the code flow's");

        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);
        user.GetProperty("userId").GetString().ShouldBe(ProcureAi.Contracts.DemoUsers.Tom);
        user.GetProperty("name").GetString().ShouldBe("Tom Mokoena");
        user.GetProperty("tenantId").GetString().ShouldBe("acme");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("requester");

        // The session forwards the access token like a code-flow session does...
        (await browser.GetAsync(Api, Ct)).EnsureSuccessStatusCode();
        var before = Bearer(_gateway.Stub.Last);
        before.GetClaim("tenant").Value.ShouldBe("acme");
        before.GetClaim("azp").Value.ShouldBe(KeycloakFixture.DemoLoginClient);

        // ...and refreshes it on the server before it expires (with the client that issued it).
        _gateway.Clock.Advance(TimeSpan.FromHours(1));
        (await browser.GetAsync(Api, Ct)).EnsureSuccessStatusCode();
        var after = Bearer(_gateway.Stub.Last);
        after.EncodedToken.ShouldNotBe(before.EncodedToken);
        after.GetClaim("tenant").Value.ShouldBe("acme");
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        SessionKeys().Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("ben", "globex", "manager")]
    [InlineData("thandi", "acme", "finance")]
    [InlineData("admin", "acme", "admin")]
    public async Task Each_listed_key_signs_in_its_own_user_in_its_tenant(string key, string tenant, string role)
    {
        var browser = Browser();

        using var response = await DemoLoginAsync(browser, key);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);
        user.GetProperty("tenantId").GetString().ShouldBe(tenant);
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain(role);
    }

    [Theory]
    [InlineData("admin@acme.test")]
    [InlineData("tom@acme.test")]
    [InlineData("Tom")]
    [InlineData("TOM")]
    [InlineData("tom ")]
    [InlineData(" tom")]
    [InlineData("to")]
    [InlineData("approver")]
    [InlineData("service-account-procure-internal")]
    [InlineData("newbie@demo.test")]
    [InlineData("")]
    public async Task Unlisted_user_is_rejected(string user)
    {
        var browser = Browser();

        using var response = await DemoLoginAsync(browser, user);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        SessionKeys().ShouldBeEmpty();
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"user\":null}")]
    [InlineData("{\"user\":42}")]
    [InlineData("{\"username\":\"tom@acme.test\",\"password\":\"x\"}")]
    [InlineData("not json")]
    public async Task Malformed_body_is_rejected_without_a_session(string body)
    {
        var browser = Browser();
        var (_, xsrf) = await UsersAsync(browser);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/demo-login")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-XSRF-TOKEN", xsrf);

        using var response = await browser.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SessionKeys().ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabled_demo_login_is_404()
    {
        // The gateway's own default: off.
        await using var off = await GatewayHost.StartAsync(keycloak, redis);
        var browser = Browser(off);

        using var users = await browser.GetAsync("/bff/demo-login/users", Ct);
        using var login = await browser.SendAsync(Post("/bff/demo-login", new { user = "tom" }, xsrf: null), Ct);
        using var loginWithHeader = await browser.SendAsync(Post("/bff/demo-login", new { user = "tom" }, xsrf: "anything"), Ct);

        users.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        login.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        loginWithHeader.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        users.Headers.Contains("Set-Cookie").ShouldBeFalse("no XSRF token is handed out either");
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demo_login_without_xsrf_is_rejected()
    {
        // A cross-site form post: the browser may send the cookies, but another site cannot read the token to set the header.
        var browser = Browser();
        await UsersAsync(browser);

        using var missing = await browser.SendAsync(Post("/bff/demo-login", new { user = "tom" }, xsrf: null), Ct);
        using var wrong = await browser.SendAsync(Post("/bff/demo-login", new { user = "tom" }, xsrf: "forged"), Ct);
        using var form = await browser.PostAsync(
            "/bff/demo-login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "tom" }), Ct);
        // No cookies at all (a fresh browser): the header alone is not enough.
        var (_, xsrf) = await UsersAsync(Browser());
        using var headerOnly = await Browser().SendAsync(Post("/bff/demo-login", new { user = "tom" }, xsrf), Ct);

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        form.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        headerOnly.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SessionKeys().ShouldBeEmpty();
    }

    [Fact]
    public async Task Logout_everywhere_revokes_demo_session()
    {
        var demo = Browser();
        (await DemoLoginAsync(demo, "tom")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var codeFlow = Browser();
        using (var driver = new LoginDriver(keycloak))
        {
            await driver.LoginAsync(codeFlow, "tom@acme.test");
        }

        var other = Browser();
        (await DemoLoginAsync(other, "maria")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionKeys().Length.ShouldBe(3);

        using var response = await demo.SendAsync(Post("/bff/logout-all", null, await SessionXsrfAsync(demo)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revoked").GetInt32().ShouldBe(2);
        (await demo.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await codeFlow.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await other.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_ends_the_demo_session()
    {
        var browser = Browser();
        (await DemoLoginAsync(browser, "tom")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await browser.SendAsync(Post("/bff/logout", null, await SessionXsrfAsync(browser)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionKeys().ShouldBeEmpty();
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demo_login_replaces_an_existing_session_of_the_browser()
    {
        var browser = Browser();
        (await DemoLoginAsync(browser, "tom")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var xsrf = await SessionXsrfAsync(browser);

        using var response = await browser.SendAsync(Post("/bff/demo-login", new { user = "maria" }, xsrf), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct)).GetProperty("name").GetString().ShouldBe("Maria Naidoo");
        SessionKeys().Length.ShouldBe(1, "Tom's session is replaced, not left behind in Redis");

        // Tom's "log out everywhere" from another browser no longer reaches this browser, which is Maria's now.
        var tom = Browser();
        (await DemoLoginAsync(tom, "tom")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var revoke = await tom.SendAsync(Post("/bff/logout-all", null, await SessionXsrfAsync(tom)), Ct);
        (await revoke.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revoked").GetInt32().ShouldBe(1);
        (await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct)).GetProperty("name").GetString().ShouldBe("Maria Naidoo");
    }

    [Fact]
    public async Task Token_with_another_tenant_than_configured_makes_no_session()
    {
        // Every login yields exactly the tenant its entry names (pre-flight ruling P5); a mismatch fails closed.
        var settings = Enabled();
        settings["DemoLogin:Users:0:Tenant"] = "globex";
        await using var mismatched = await GatewayHost.StartAsync(keycloak, redis, settings);
        var browser = Browser(mismatched);

        using var response = await DemoLoginAsync(browser, "tom");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("globex");
        body.ShouldNotContain("acme");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Keycloak_refusal_is_not_passed_to_the_browser()
    {
        var settings = Enabled();
        settings["DemoLogin:Password"] = "wrong-password";
        await using var refused = await GatewayHost.StartAsync(keycloak, redis, settings);
        var browser = Browser(refused);

        using var response = await DemoLoginAsync(browser, "tom");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("invalid_grant", Case.Insensitive);
        body.ShouldNotContain("credentials", Case.Insensitive);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Demo_client_cannot_start_browser_flow()
    {
        // The demo client has no browser flow: Keycloak itself refuses an authorization request for it.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorize = $"{keycloak.BaseUrl}/realms/{KeycloakFixture.Realm}/protocol/openid-connect/auth" +
                        $"?client_id={KeycloakFixture.DemoLoginClient}&response_type=code&scope=openid" +
                        $"&redirect_uri={Uri.EscapeDataString("https://localhost:5001/signin-oidc")}&state=s&nonce=n";

        using var response = await http.GetAsync(authorize, Ct);

        LoginDriver.IsRedirect(response.StatusCode).ShouldBeFalse("no redirect to a login form or back with a code");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        LoginDriver.FindFormAction(await response.Content.ReadAsStringAsync(Ct), "login-actions/authenticate").ShouldBeNull();
    }

    [Fact]
    public async Task Gateway_client_cannot_use_the_password_grant()
    {
        // Only the separate demo client has direct access grants; the BFF client keeps the code flow only.
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{keycloak.BaseUrl}/realms/{KeycloakFixture.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "procure-gateway",
                ["client_secret"] = keycloak.GatewaySecret,
                ["username"] = "tom@acme.test",
                ["password"] = keycloak.DemoPassword,
            }), Ct);

        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Theory]
    [InlineData("DemoLogin:ClientSecret", "")]
    [InlineData("DemoLogin:Password", "")]
    [InlineData("DemoLogin:ClientId", "")]
    [InlineData("DemoLogin:Users:0:Key", "Tom@Acme")]
    [InlineData("DemoLogin:Users:1:Key", "tom")]
    [InlineData("DemoLogin:Users:0:Username", "")]
    [InlineData("DemoLogin:Users:0:Label", "")]
    [InlineData("DemoLogin:Users:0:Tenant", "")]
    [InlineData("DemoLogin:Users:0:TenantName", "")]
    public async Task Enabled_demo_login_with_invalid_configuration_fails_at_startup(string key, string value)
    {
        var settings = Enabled();
        settings[key] = value;

        await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => GatewayHost.StartAsync(keycloak, redis, settings));
    }

    [Fact]
    public async Task Disabled_demo_login_needs_no_secret_or_password()
    {
        await using var off = await GatewayHost.StartAsync(keycloak, redis, new()
        {
            ["DemoLogin:Enabled"] = "false",
            ["DemoLogin:ClientSecret"] = "",
            ["DemoLogin:Password"] = "",
        });

        (await Browser(off).GetAsync("/bff/demo-login/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private RedisKey[] SessionKeys()
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString + ",allowAdmin=true");
        var server = connection.GetServer(connection.GetEndPoints().First());
        return [.. server.Keys(pattern: $"{_gateway.KeyPrefix}session:*")];
    }
}
