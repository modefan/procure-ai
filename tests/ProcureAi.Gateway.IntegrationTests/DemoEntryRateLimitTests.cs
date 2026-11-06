using System.Net;
using System.Net.Http.Json;
using ProcureAi.Contracts;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The <c>demo-entry</c> rate limit (plan 07 Task 0a, plan 08 Task 6): <c>POST /bff/demo-login</c> and <c>POST /bff/switch-tenant</c> each allow
/// <c>RateLimiting:DemoEntry:PermitLimit</c> calls per <c>RateLimiting:DemoEntry:Window</c> and client (an IPv4 address or an IPv6 /64, after
/// the forwarded headers of a trusted proxy) and <c>GlobalPermitLimit</c> over every client, on top of the gateway's general per-IP limit. The
/// test host lifts the limit for the other tests; these use the gateway's own configuration (30 per minute per client, 300 in all), except
/// where a test lowers a limit to keep the number of Keycloak logins small. Real Keycloak and Redis, stub Access.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class DemoEntryRateLimitTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const int Limit = 30;

    private readonly List<IAsyncDisposable> _hosts = [];
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>Demo login on, and the demo-entry limit as the gateway's appsettings.json sets it (the nulls remove the test host's lifts).</summary>
    private async Task<GatewayHost> StartAsync(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DemoLogin:Enabled"] = "true",
            ["DemoLogin:ClientSecret"] = keycloak.DemoLoginSecret,
            ["DemoLogin:Password"] = keycloak.DemoPassword,
            ["RateLimiting:DemoEntry:PermitLimit"] = null,
            ["RateLimiting:DemoEntry:GlobalPermitLimit"] = null,
        };
        foreach (var (key, value) in more ?? [])
        {
            settings[key] = value;
        }

        var gateway = await GatewayHost.StartAsync(keycloak, redis, settings);
        _hosts.Add(gateway);
        gateway.Access.SetUserTenants(DemoUsers.Tom, ("acme", "Acme Corp"));
        gateway.Access.SetTenantName("acme", "Acme Corp");
        return gateway;
    }

    private HttpClient Browser(GatewayHost gateway)
    {
        var browser = gateway.NewBrowser();
        _disposables.Add(browser);
        return browser;
    }

    private static string Xsrf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    /// <summary>The welcome page's demo login in a fresh browser, optionally from <paramref name="forwardedFor"/> behind the trusted proxy.</summary>
    private async Task<HttpResponseMessage> DemoLoginAsync(GatewayHost gateway, string? forwardedFor = null, HttpClient? browser = null)
    {
        browser ??= Browser(gateway);
        using var users = new HttpRequestMessage(HttpMethod.Get, "/bff/demo-login/users");
        using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/demo-login") { Content = JsonContent.Create(new { user = "tom" }) };
        if (forwardedFor is not null)
        {
            users.Headers.Add("X-Forwarded-For", forwardedFor);
            login.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var listed = await browser.SendAsync(users, Ct);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.Headers.Add("X-XSRF-TOKEN", Xsrf(listed));
        return await browser.SendAsync(login, Ct);
    }

    private static async Task<HttpResponseMessage> SwitchAsync(HttpClient browser)
    {
        using var user = await browser.GetAsync("/bff/user", Ct);
        user.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/switch-tenant")
        {
            Content = JsonContent.Create(new { tenant = "acme", returnUrl = "/app/dashboard" }),
        };
        request.Headers.Add("X-XSRF-TOKEN", Xsrf(user));
        return await browser.SendAsync(request, Ct);
    }

    private static void ShouldBeLimited(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        retryAfter.ShouldBeInRange(1, 60, "within the one-minute window");
    }

    [Fact]
    public async Task The_31st_from_one_ip_is_429()
    {
        var gateway = await StartAsync();

        // Every attempt in a browser of its own: the limit is the client address's, not the session's.
        for (var i = 1; i <= Limit; i++)
        {
            using var allowed = await DemoLoginAsync(gateway);
            allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"login {i}: {await allowed.Content.ReadAsStringAsync(Ct)}");
        }

        using var refused = await DemoLoginAsync(gateway);

        ShouldBeLimited(refused);
        refused.Headers.Contains("Set-Cookie").ShouldBeFalse("a refused login creates no session");
    }

    [Fact]
    public async Task Switch_tenant_is_limited_to_30_per_minute_per_ip()
    {
        var gateway = await StartAsync();
        var first = Browser(gateway);
        var second = Browser(gateway);
        (await DemoLoginAsync(gateway, browser: first)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DemoLoginAsync(gateway, browser: second)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        for (var i = 1; i <= Limit; i++)
        {
            using var allowed = await SwitchAsync(first);
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK, $"switch {i}: {await allowed.Content.ReadAsStringAsync(Ct)}");
        }

        // Another session from the same address shares the allowance; and the demo login keeps an allowance of its own.
        using var refused = await SwitchAsync(second);

        ShouldBeLimited(refused);
        using var stillAllowed = await DemoLoginAsync(gateway);
        stillAllowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "each endpoint counts its own calls");
    }

    [Fact]
    public async Task Demo_entry_limit_partitions_by_forwarded_ip()
    {
        // The test server's connection address (GatewayHost) is the trusted proxy, so X-Forwarded-For names the client.
        var gateway = await StartAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10" });

        for (var i = 1; i <= Limit; i++)
        {
            using var allowed = await DemoLoginAsync(gateway, "198.51.100.7");
            allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"login {i}");
        }

        using var refused = await DemoLoginAsync(gateway, "198.51.100.7");
        using var otherClient = await DemoLoginAsync(gateway, "198.51.100.8");

        ShouldBeLimited(refused);
        otherClient.StatusCode.ShouldBe(HttpStatusCode.NoContent, "another client behind the same proxy has its own allowance");
    }

    [Fact]
    public async Task Ten_logins_from_one_ip_within_a_minute_succeed()
    {
        // Ten reviewers behind one NAT (one address) all get in; the plan 07 limit (10) left no room for a second try.
        var gateway = await StartAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10" });

        for (var i = 1; i <= 11; i++)
        {
            using var allowed = await DemoLoginAsync(gateway, "198.51.100.20");
            allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"login {i} from one address");
        }
    }

    [Fact]
    public async Task Ipv6_addresses_in_one_64_share_a_bucket()
    {
        // A small per-client limit keeps the Keycloak logins few; the rule is the same at 30.
        var gateway = await StartAsync(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10",
            ["RateLimiting:DemoEntry:PermitLimit"] = "2",
        });

        // Two addresses of one /64 (the host picked other interface identifiers) use up one allowance ...
        (await DemoLoginAsync(gateway, "2001:db8:1:2::1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DemoLoginAsync(gateway, "2001:db8:1:2:aaaa:bbbb:cccc:dddd")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var refused = await DemoLoginAsync(gateway, "2001:db8:1:2:ffff::7");
        // ... while the next /64 has its own.
        using var otherPrefix = await DemoLoginAsync(gateway, "2001:db8:1:3::1");

        ShouldBeLimited(refused);
        otherPrefix.StatusCode.ShouldBe(HttpStatusCode.NoContent, "another /64 is another client");
    }

    [Fact]
    public async Task The_global_limit_refuses_every_client_once_spent()
    {
        var gateway = await StartAsync(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10",
            ["RateLimiting:DemoEntry:PermitLimit"] = "2",
            ["RateLimiting:DemoEntry:GlobalPermitLimit"] = "3",
        });

        // A client over its own limit does not spend the global allowance ...
        (await DemoLoginAsync(gateway, "198.51.100.30")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DemoLoginAsync(gateway, "198.51.100.30")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var ownLimit = await DemoLoginAsync(gateway, "198.51.100.30");
        ShouldBeLimited(ownLimit);
        (await DemoLoginAsync(gateway, "198.51.100.31")).StatusCode.ShouldBe(HttpStatusCode.NoContent, "the third login of the window");

        // ... but once three logins went through, a fresh client is refused too.
        using var refused = await DemoLoginAsync(gateway, "198.51.100.32");

        ShouldBeLimited(refused);
    }

    [Fact]
    public async Task A_global_limit_below_the_client_limit_fails_at_startup()
    {
        var start = () => StartAsync(new()
        {
            ["RateLimiting:DemoEntry:PermitLimit"] = "30",
            ["RateLimiting:DemoEntry:GlobalPermitLimit"] = "10",
        });

        var error = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(start);
        error.Message.ShouldContain("GlobalPermitLimit");
    }
}
