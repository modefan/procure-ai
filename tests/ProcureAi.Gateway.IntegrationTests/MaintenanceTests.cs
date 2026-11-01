using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StackExchange.Redis;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The maintenance window of the nightly demo reset (plan 07 Task 0b, carry-over 6): while <c>ProcureAi.DemoData reset</c> holds the flag
/// (<c>PUT api/v1/gateway/maintenance</c> with the admin key; Redis key with a TTL), every write to <c>/api/**</c> and <c>/bff/**</c> except
/// <c>/bff/logout</c> gets 503 ProblemDetails with <c>Retry-After: 30</c>; reads and calls with the valid admin key pass (the seed writes
/// through the gateway with it). A crashed reset never locks the app: the flag expires by itself. Real Keycloak and Redis, stub services.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class MaintenanceTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Api = "/api/v1/requests";
    private const string Maintenance = "/api/v1/gateway/maintenance";

    private readonly List<IDisposable> _disposables = [];
    private readonly string _flagKey = $"test-{Guid.NewGuid():N}:maintenance";
    private GatewayHost _gateway = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _gateway = await StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        await _gateway.DisposeAsync();
    }

    private Task<GatewayHost> StartAsync(string? ttl = null) => GatewayHost.StartAsync(keycloak, redis, new Dictionary<string, string?>
    {
        ["Auth:AdminApiKeySha256"] = TestAuth.ApiKeySha256,
        ["DemoReset:MaintenanceKey"] = _flagKey,
        ["DemoReset:MaintenanceTtl"] = ttl,
    });

    [Fact]
    public async Task Writes_get_503_while_maintenance_flag_is_set()
    {
        var tom = await SignedInAsync();
        var xsrf = await XsrfTokenAsync(tom);
        await SetFlagAsync();
        var seenBefore = _gateway.Stub.Seen.Count;

        // A signed-in user's write, a bearer-token write, and a forged admin key: none reaches the service.
        await ExpectMaintenanceAsync(await tom.SendAsync(Request(HttpMethod.Post, Api, xsrf), Ct));
        await ExpectMaintenanceAsync(await tom.SendAsync(Request(HttpMethod.Put, $"{Api}/{Guid.NewGuid()}", xsrf), Ct));
        await ExpectMaintenanceAsync(await tom.SendAsync(Request(HttpMethod.Delete, $"{Api}/{Guid.NewGuid()}", xsrf), Ct));
        await ExpectMaintenanceAsync(await Browser().SendAsync(Request(HttpMethod.Post, Api, headers: [("Authorization", "Bearer a-token")]), Ct));
        await ExpectMaintenanceAsync(await Browser().SendAsync(
            Request(HttpMethod.Post, Api, headers: [("X-Api-Key", "not-the-admin-key-but-long-enough-0123456789"), ("X-Tenant-Id", "acme")]), Ct));
        // The BFF's own writes too (here the tenant switch and logout-everywhere), but logout stays possible.
        await ExpectMaintenanceAsync(await tom.SendAsync(Request(HttpMethod.Post, "/bff/switch-tenant", xsrf), Ct));
        await ExpectMaintenanceAsync(await tom.SendAsync(Request(HttpMethod.Post, "/bff/logout-all", xsrf), Ct));
        _gateway.Stub.Seen.Count.ShouldBe(seenBefore, "no write reached a service during the maintenance window");

        using var logout = await tom.SendAsync(Request(HttpMethod.Post, "/bff/logout", xsrf), Ct);
        logout.StatusCode.ShouldNotBe(HttpStatusCode.ServiceUnavailable, await logout.Content.ReadAsStringAsync(Ct));

        // Once the reset clears the flag, writes go through again.
        await ClearFlagAsync();
        using var after = await Browser().SendAsync(Request(HttpMethod.Post, Api, headers: [("Authorization", "Bearer a-token")]), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Stub.Last.Method.ShouldBe("POST");
    }

    [Fact]
    public async Task Admin_key_writes_pass_during_maintenance()
    {
        await SetFlagAsync();

        using var response = await Browser().SendAsync(
            Request(HttpMethod.Post, Api, headers: [("X-Api-Key", TestAuth.ApiKey), ("X-Tenant-Id", "acme")]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        _gateway.Stub.Last.Method.ShouldBe("POST");
        _gateway.Stub.Last.Path.ShouldBe(Api);
    }

    [Fact]
    public async Task Gets_pass_during_maintenance()
    {
        var tom = await SignedInAsync();
        await SetFlagAsync();

        using var list = await tom.GetAsync(Api, Ct);
        using var user = await tom.GetAsync("/bff/user", Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.Content.ReadAsStringAsync(Ct));
        user.StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Stub.Last.Method.ShouldBe("GET");
    }

    [Fact]
    public async Task Maintenance_flag_expires_by_itself()
    {
        // A reset that crashes never clears the flag: its TTL does.
        await using var gateway = await StartAsync(ttl: "00:00:02");
        using var browser = gateway.NewBrowser();
        using (var set = await browser.SendAsync(Admin(HttpMethod.Put), Ct))
        {
            set.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(_flagKey);
        ttl.ShouldNotBeNull("the flag is set with an expiry");
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
        DateTimeOffset.Parse((string)(await connection.GetDatabase().StringGetAsync(_flagKey))!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "the value is the ISO time it was set");
        await ExpectMaintenanceAsync(await browser.SendAsync(Request(HttpMethod.Post, Api, headers: [("Authorization", "Bearer a-token")]), Ct));

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        using var after = await browser.SendAsync(Request(HttpMethod.Post, Api, headers: [("Authorization", "Bearer a-token")]), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.OK, await after.Content.ReadAsStringAsync(Ct));
        using var status = await browser.SendAsync(Admin(HttpMethod.Get), Ct);
        (await status.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_admin_key_sets_or_clears_the_flag()
    {
        var tom = await SignedInAsync();
        var xsrf = await XsrfTokenAsync(tom);

        using var user = await tom.SendAsync(Request(HttpMethod.Put, Maintenance, xsrf), Ct);
        using var forged = await Browser().SendAsync(Request(HttpMethod.Put, Maintenance, headers: [("X-Api-Key", "not-the-admin-key-but-long-enough-0123456789")]), Ct);
        using var anonymous = await Browser().SendAsync(Request(HttpMethod.Get, Maintenance), Ct);

        user.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        forged.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await IsActiveAsync()).ShouldBeFalse();

        await SetFlagAsync();
        (await IsActiveAsync()).ShouldBeTrue();
        // The forged key cannot lift it either: during the window its DELETE is a write like any other (503); only the admin key's lifts it.
        using var forgedClear = await Browser().SendAsync(Request(HttpMethod.Delete, Maintenance, headers: [("X-Api-Key", "not-the-admin-key-but-long-enough-0123456789")]), Ct);
        forgedClear.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await IsActiveAsync()).ShouldBeTrue();
        await ClearFlagAsync();
        (await IsActiveAsync()).ShouldBeFalse();
        await ClearFlagAsync(); // twice is safe
    }

    private static async Task ExpectMaintenanceAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, body);
            response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(30));
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var problem = JsonDocument.Parse(body).RootElement;
            problem.GetProperty("title").GetString().ShouldBe("The demo is being reset");
            problem.GetProperty("status").GetInt32().ShouldBe(503);
        }
    }

    private async Task SetFlagAsync()
    {
        using var response = await Browser().SendAsync(Admin(HttpMethod.Put), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task ClearFlagAsync()
    {
        using var response = await Browser().SendAsync(Admin(HttpMethod.Delete), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<bool> IsActiveAsync()
    {
        using var response = await Browser().SendAsync(Admin(HttpMethod.Get), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("active").GetBoolean();
    }

    private static HttpRequestMessage Admin(HttpMethod method) => Request(method, Maintenance, headers: [("X-Api-Key", TestAuth.ApiKey)]);

    private HttpClient Browser()
    {
        var browser = _gateway.NewBrowser();
        _disposables.Add(browser);
        return browser;
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var browser = Browser();
        var driver = new LoginDriver(keycloak);
        _disposables.Add(driver);
        await driver.LoginAsync(browser, "tom@acme.test");
        return browser;
    }

    private static async Task<string> XsrfTokenAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/user", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? xsrf = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url) { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { }) };
        if (xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}
