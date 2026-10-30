using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Diagnostics;

[Collection(RedisCollection.Name)]
public sealed partial class DevPanelHeaderTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] DevHeaders =
    [
        DevPanelHeaders.Cache, DevPanelHeaders.Coalesced, DevPanelHeaders.Idempotent,
        DevPanelHeaders.Policy, DevPanelHeaders.Tenant, DevPanelHeaders.Instance,
    ];

    [GeneratedRegex("^[0-9a-f]{32}\\z")]
    private static partial Regex TraceIdShape();

    private async Task<WebApplication> StartAsync(bool enabled = true, Action<WebApplicationBuilder>? configure = null) =>
        await TestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["DevPanel:Enabled"] = enabled ? "true" : "false",
                ["RateLimiting:PermitLimit"] = "100000",
            },
            configure: builder =>
            {
                builder.AddProcureAiCaching();
                configure?.Invoke(builder);
            });

    private static HttpClient Client(WebApplication app, bool devPanel = true, string tenant = "acme")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestAuth.ApiKey);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        if (devPanel)
        {
            client.DefaultRequestHeaders.Add(DevPanelHeaders.Request, "1");
        }

        return client;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Trace_id_header_on_every_response()
    {
        await using var app = await StartAsync(enabled: false);
        using var client = Client(app, devPanel: false);

        foreach (var url in new[] { "/api/v1/test/ping", "/api/v1/test/crash", "/api/v1/nothing-here" })
        {
            using var response = await client.GetAsync(url, Ct);
            TraceIdShape().IsMatch(Header(response, DevPanelHeaders.TraceId) ?? "").ShouldBeTrue(url);
        }
    }

    [Fact]
    public async Task No_dev_headers_without_request_header()
    {
        await using var app = await StartAsync();
        using var client = Client(app, devPanel: false);

        using var response = await client.GetAsync($"/api/v1/coalesce/get?id={NewId()}&delayMs=0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, DevPanelHeaders.TraceId).ShouldNotBeNull();
        foreach (var name in DevHeaders)
        {
            Header(response, name).ShouldBeNull(name);
        }
    }

    [Fact]
    public async Task No_dev_headers_when_disabled()
    {
        await using var app = await StartAsync(enabled: false);
        using var client = Client(app);

        using var response = await client.GetAsync($"/api/v1/coalesce/get?id={NewId()}&delayMs=0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, DevPanelHeaders.TraceId).ShouldNotBeNull();
        foreach (var name in DevHeaders)
        {
            Header(response, name).ShouldBeNull(name);
        }
    }

    [Fact]
    public async Task Cached_get_reports_miss_then_hit_with_age()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        var url = $"/api/v1/coalesce/get?id={NewId()}&delayMs=0";

        using var first = await client.GetAsync(url, Ct);
        await Task.Delay(50, Ct);
        using var second = await client.GetAsync(url, Ct);

        Header(first, DevPanelHeaders.Cache).ShouldBe("miss");
        Header(first, DevPanelHeaders.Coalesced).ShouldBeNull();
        var hit = Header(second, DevPanelHeaders.Cache)!;
        Regex.IsMatch(hit, "^hit; age-ms=\\d+\\z").ShouldBeTrue(hit);
        long.Parse(hit["hit; age-ms=".Length..]).ShouldBeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public async Task Parallel_identical_get_reports_joined_and_counts_stat()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        var stats = app.Services.GetRequiredService<IDailyStats>();
        var today = ((RedisDailyStats)stats).Today;
        var before = (await stats.GetAsync(today)).GetValueOrDefault(DailyStatNames.Coalesced);
        var url = $"/api/v1/coalesce/get?id={NewId()}&delayMs=800";

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetAsync(url, Ct)));

        var joined = responses.Count(r => Header(r, DevPanelHeaders.Coalesced) == "joined");
        responses.Count(r => Header(r, DevPanelHeaders.Cache) == "miss" && Header(r, DevPanelHeaders.Coalesced) is null).ShouldBe(1);
        joined.ShouldBe(4);
        var after = (await stats.GetAsync(today)).GetValueOrDefault(DailyStatNames.Coalesced);
        (after - before).ShouldBe(joined);
    }

    [Fact]
    public async Task Idempotent_replay_reports_replayed_and_counts_stat()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        var stats = app.Services.GetRequiredService<IDailyStats>();
        var today = ((RedisDailyStats)stats).Today;
        var before = (await stats.GetAsync(today)).GetValueOrDefault(DailyStatNames.Coalesced);
        var id = NewId();
        var key = Guid.NewGuid().ToString();

        HttpRequestMessage Post()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/coalesce/post")
            {
                Content = JsonContent.Create(new CoalescingController.PostRequest(id, DelayMs: 0)),
            };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        using var first = await client.SendAsync(Post(), Ct);
        using var second = await client.SendAsync(Post(), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        Header(first, DevPanelHeaders.Idempotent).ShouldBeNull();
        Header(second, DevPanelHeaders.Idempotent).ShouldBe("replayed");
        var after = (await stats.GetAsync(today)).GetValueOrDefault(DailyStatNames.Coalesced);
        (after - before).ShouldBe(1);
    }

    [Fact]
    public async Task Policy_and_tenant_headers_name_the_checked_permission()
    {
        await using var app = await StartAsync();
        using var client = Client(app, tenant: "globex");

        using var one = await client.GetAsync("/api/v1/devpanel/one-policy", Ct);
        using var two = await client.GetAsync("/api/v1/devpanel/two-policies", Ct);

        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(one, DevPanelHeaders.Policy).ShouldBe("requests.read");
        Header(one, DevPanelHeaders.Tenant).ShouldBe("globex");
        Header(one, DevPanelHeaders.Instance).ShouldStartWith("test-service/");
        var joined = Header(two, DevPanelHeaders.Policy)!;
        joined.ShouldContain(", ");
        joined.Split(", ").Order().ToArray().ShouldBe(["requests.create", "requests.read"]);
    }

    [Fact]
    public async Task Redis_down_does_not_fail_request()
    {
        // The stats store throws; joined GETs must still succeed.
        await using var app = await StartAsync(configure: builder =>
            builder.Services.AddSingleton<IDailyStats, ThrowingStats>());
        using var client = Client(app);
        var url = $"/api/v1/coalesce/get?id={NewId()}&delayMs=500";

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync(url, Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);

        // And the real store, pointed at a Redis that is not there, neither throws nor hangs.
        var config = StackExchange.Redis.ConfigurationOptions.Parse("127.0.0.1:1");
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 200;
        config.AsyncTimeout = 500;
        await using var dead = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(config);
        var stats = new RedisDailyStats(dead, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisDailyStats>.Instance);
        await stats.IncrementAsync("coalesced");
        (await stats.GetAsync(stats.Today)).ShouldBeEmpty();
    }

    private sealed class ThrowingStats : IDailyStats
    {
        public Task IncrementAsync(string name) => throw new InvalidOperationException("redis is down");

        public Task<IReadOnlyDictionary<string, long>> GetAsync(DateOnly day) => throw new InvalidOperationException("redis is down");

        public DateOnly Today => new(2026, 10, 30);
    }
}
