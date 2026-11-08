using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// Plan 08 Task 12b: the system on/off switch, enforced by the gateway. While Access says the demo is off, every browser call to
/// <c>/api/**</c>, <c>/bff/**</c> and the hubs gets 503 ProblemDetails of type <see cref="SystemDisabledType"/> with <c>Retry-After</c>, before
/// authentication and before anything is proxied. Always let through: the switch itself (read and write), health, Swagger UI, the web app's
/// files and calls with the valid admin API key. The gateway reads the switch from Access (stub here) and caches it for
/// <c>SystemAvailability:CacheDuration</c>; with Access down it keeps the last answer (fail-safe), and with no answer ever it lets calls
/// through (fail open).
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class SystemSwitchTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string SystemDisabledType = "https://procure-ai/problems/system-disabled";
    private const string Api = "/api/v1/requests";
    private const string Availability = "/api/v1/system/availability";
    private const string ForgedKey = "not-the-admin-key-but-long-enough-0123456789";

    private readonly List<IDisposable> _disposables = [];
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

    private Task<GatewayHost> StartAsync() => GatewayHost.StartAsync(keycloak, redis, new Dictionary<string, string?>
    {
        ["Auth:AdminApiKeySha256"] = TestAuth.ApiKeySha256,
        ["SystemAvailability:CacheDuration"] = "00:00:01",
        ["SystemAvailability:FailSafeThrottleDuration"] = "00:00:01",
        ["SystemAvailability:RetryAfter"] = "00:01:00",
    });

    [Fact]
    public async Task Browser_calls_get_503_while_the_demo_is_off()
    {
        var tom = await SignedInAsync();
        await SwitchOffAsync("Back on Monday.");
        var seenBefore = _gateway.Stub.Seen.Count;

        await ExpectDisabledAsync(await tom.GetAsync(Api, Ct), "Back on Monday.");
        await ExpectDisabledAsync(await Browser().SendAsync(Request(HttpMethod.Post, Api, ("Authorization", "Bearer a-token")), Ct), "Back on Monday.");
        await ExpectDisabledAsync(await tom.GetAsync("/bff/user", Ct), "Back on Monday.");
        await ExpectDisabledAsync(await Browser().GetAsync("/bff/login?returnUrl=%2F", Ct), "Back on Monday.");
        await ExpectDisabledAsync(await tom.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", null, Ct), "Back on Monday.");
        await ExpectDisabledAsync(await tom.GetAsync("/hubs/notifications?id=abc", Ct), "Back on Monday.");
        // A forged admin key is a browser call like any other.
        await ExpectDisabledAsync(await Browser().SendAsync(Request(HttpMethod.Get, Api, ("X-Api-Key", ForgedKey), ("X-Tenant-Id", "acme")), Ct), "Back on Monday.");

        _gateway.Stub.Seen.Count.ShouldBe(seenBefore, "no call reached a service while the demo was off");
    }

    [Fact]
    public async Task Without_a_message_the_503_has_the_default_text()
    {
        await SwitchOffAsync(null);

        using var response = await Browser().GetAsync(Api, Ct);

        var problem = await ExpectDisabledAsync(response, expectedDetail: null);
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_switch_health_swagger_and_the_web_app_stay_reachable_while_off()
    {
        await SwitchOffAsync("Off.");
        var browser = Browser();

        using (var read = await browser.GetAsync(Availability, Ct))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
            (await read.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("enabled").GetBoolean().ShouldBeFalse();
        }

        using (var live = await browser.GetAsync("/health/live", Ct))
        {
            live.StatusCode.ShouldNotBe(HttpStatusCode.ServiceUnavailable);
        }

        using (var swagger = await browser.GetAsync("/swagger/index.html", Ct))
        {
            swagger.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach (var file in new[] { "/", "/config.json", "/main-ABC123.js", "/maintenance" })
        {
            using var page = await browser.GetAsync(file, Ct);
            page.StatusCode.ShouldBe(HttpStatusCode.OK, file);
        }

        // The admin key switches it back on through the gateway, and then browser calls pass again.
        using var on = new HttpRequestMessage(HttpMethod.Put, Availability) { Content = JsonContent.Create(new { enabled = true }) };
        on.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        using (var put = await browser.SendAsync(on, Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        }

        _gateway.Access.SystemEnabled.ShouldBeTrue("the PUT reached Access");
    }

    [Fact]
    public async Task Admin_key_calls_pass_while_off()
    {
        await SwitchOffAsync("Off.");

        using var response = await Browser().SendAsync(Request(HttpMethod.Post, Api, ("X-Api-Key", TestAuth.ApiKey), ("X-Tenant-Id", "acme")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        _gateway.Stub.Last.Path.ShouldBe(Api);
    }

    [Fact]
    public async Task Switching_back_on_lets_calls_through_within_the_cache_duration()
    {
        await SwitchOffAsync("Off.");
        await ExpectDisabledAsync(await Browser().GetAsync(Api, Ct), "Off.");

        _gateway.Access.SystemEnabled = true;
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        using var response = await Browser().SendAsync(Request(HttpMethod.Get, Api, ("Authorization", "Bearer a-token")), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task With_Access_down_and_no_answer_yet_calls_pass()
    {
        _gateway.Access.AvailabilityDown = true;

        using var response = await Browser().SendAsync(Request(HttpMethod.Get, Api, ("Authorization", "Bearer a-token")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "fail open: an unreadable switch never locks the demo");
        _gateway.Access.AvailabilityCalls.ShouldBeGreaterThan(0, "the gateway did ask");
    }

    [Fact]
    public async Task With_Access_down_the_last_answer_is_kept()
    {
        await SwitchOffAsync("Kept.");
        await ExpectDisabledAsync(await Browser().GetAsync(Api, Ct), "Kept.");

        _gateway.Access.AvailabilityDown = true;
        _gateway.Access.SystemEnabled = true;
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        await ExpectDisabledAsync(await Browser().GetAsync(Api, Ct), "Kept.");
    }

    [Fact]
    public async Task The_gateway_reads_the_switch_as_itself_and_proxied_reads_stay_anonymous()
    {
        using (var call = await Browser().SendAsync(Request(HttpMethod.Get, Api, ("Authorization", "Bearer a-token")), Ct))
        {
            call.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Its own read: the service token and its name, so Access counts it in the gateway service account's partition, not in the
        // anonymous one that every proxied browser read shares (they all come from the gateway's address).
        var own = _gateway.Access.AvailabilityReads.ShouldHaveSingleItem();
        own.HasBearer.ShouldBeTrue();
        own.CallingService.ShouldBe("gateway");

        // A visitor's read of the switch is proxied as it came: no token of the gateway's, and a claimed calling service is no credential.
        using (var read = await Browser().SendAsync(Request(HttpMethod.Get, Availability, ("X-Calling-Service", "gateway")), Ct))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        _gateway.Access.AvailabilityReads.Count.ShouldBe(2);
        _gateway.Access.AvailabilityReads[^1].HasBearer.ShouldBeFalse("the gateway never lends its token to a proxied call");
    }

    [Fact]
    public async Task Without_a_service_token_the_switch_is_still_read()
    {
        _gateway.Access.TokenDown = true;
        _gateway.Access.SystemMessage = "Off.";
        _gateway.Access.SystemEnabled = false;

        await ExpectDisabledAsync(await Browser().GetAsync(Api, Ct), "Off.");

        var read = _gateway.Access.AvailabilityReads.ShouldHaveSingleItem();
        read.HasBearer.ShouldBeFalse("read anonymously: a Keycloak outage must not make the switch unreadable");
    }

    [Fact]
    public async Task While_on_nothing_changes()
    {
        using var response = await Browser().SendAsync(Request(HttpMethod.Get, Api, ("Authorization", "Bearer a-token")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Stub.Last.Path.ShouldBe(Api);
    }

    /// <summary>
    /// Switches the stub Access off and waits out the gateway's cache (1 s here): a call made before (the sign-in, say) may have cached
    /// "on", and the gateway sees the change only once that entry expires, as it does in production.
    /// </summary>
    private async Task SwitchOffAsync(string? message)
    {
        _gateway.Access.SystemMessage = message;
        _gateway.Access.SystemEnabled = false;
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
    }

    private static async Task<JsonElement> ExpectDisabledAsync(HttpResponseMessage response, string? expectedDetail)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, body);
            response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromMinutes(1));
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var problem = JsonDocument.Parse(body).RootElement.Clone();
            problem.GetProperty("type").GetString().ShouldBe(SystemDisabledType);
            problem.GetProperty("status").GetInt32().ShouldBe(503);
            problem.GetProperty("title").GetString().ShouldBe("This demo is switched off");
            if (expectedDetail is not null)
            {
                problem.GetProperty("detail").GetString().ShouldBe(expectedDetail);
            }

            // The stored message as such (null without one), so the web app shows the same texts however it learnt the demo is off.
            var message = problem.GetProperty("message");
            if (expectedDetail is null)
            {
                message.ValueKind.ShouldBe(JsonValueKind.Null);
            }
            else
            {
                message.GetString().ShouldBe(expectedDetail);
            }

            return problem;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

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
}
