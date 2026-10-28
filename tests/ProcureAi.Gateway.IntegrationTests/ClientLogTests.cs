using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>Collects every log event the gateway writes, so the tests can look at the properties.</summary>
public sealed class CollectingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>The entries that came from the browser.</summary>
    public IReadOnlyList<LogEvent> Browser()
    {
        lock (_events)
        {
            return _events.Where(e => e.Properties.TryGetValue("Source", out var s) && s is ScalarValue { Value: "browser" }).ToList();
        }
    }
}

[Collection(GatewayCollection.Name)]
public sealed class ClientLogTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Path = "/api/client-logs";

    private readonly CollectingSink _sink = new();
    private readonly List<IDisposable> _disposables = [];
    private GatewayHost _gateway = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }
    }

    private async Task StartAsync(Dictionary<string, string?>? extra = null) =>
        _gateway = await GatewayHost.StartAsync(keycloak, redis, extra, configureServices: services => services.AddSingleton<ILogEventSink>(_sink));

    private HttpClient Browser()
    {
        var browser = _gateway.NewBrowser();
        _disposables.Add(browser);
        return browser;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "text/plain");

    private static string Prop(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text } ? text : "";

    [Fact]
    public async Task Error_entry_is_logged_with_user_and_tenant()
    {
        await StartAsync();
        var driver = new LoginDriver(keycloak);
        _disposables.Add(driver);
        var browser = Browser();
        await driver.LoginAsync(browser, "tom@acme.test");
        var me = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        var traceId = "0af7651916cd43dd8448eb211c80319c";
        using var response = await browser.PostAsync(Path, Json(
            $$$"""[{"timestamp":"2026-10-28T10:00:00Z","level":"error","source":"RequestStore","message":"Load failed","url":"/requests?x=1","traceId":"{{{traceId}}}","data":{"status":500}}]"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var entry = _sink.Browser().ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogEventLevel.Error);
        Prop(entry, "ClientSource").ShouldBe("RequestStore");
        Prop(entry, "UserId").ShouldBe(me.GetProperty("userId").GetString());
        Prop(entry, "TenantId").ShouldBe(me.GetProperty("tenantId").GetString());
        Prop(entry, "TenantId").ShouldNotBeEmpty();
        Prop(entry, "TraceId").ShouldBe(traceId);
        entry.RenderMessage().ShouldContain("Load failed");
    }

    [Fact]
    public async Task Anonymous_entry_is_logged_without_user_and_levels_are_mapped()
    {
        await StartAsync();

        using var response = await Browser().PostAsync(Path, Json(
            """[{"level":"warn","source":"a","message":"w"},{"level":"info","source":"a","message":"i"},{"level":"bogus","source":"a","message":"b"}]"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var events = _sink.Browser();
        events.Select(e => e.Level).ShouldBe([LogEventLevel.Warning, LogEventLevel.Information, LogEventLevel.Warning]);
        events.ShouldAllBe(e => !e.Properties.ContainsKey("UserId") && !e.Properties.ContainsKey("TenantId"));
    }

    [Fact]
    public async Task Token_like_fields_are_not_logged()
    {
        await StartAsync();

        using var response = await Browser().PostAsync(Path, Json(
            """[{"level":"error","source":"s","message":"m","data":{"accessToken":"SECRET-A","nested":{"Password":"SECRET-B","ok":"visible"},"list":[{"apiKey":"SECRET-C"}],"Authorization":"SECRET-D"}}]"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var entry = _sink.Browser().ShouldHaveSingleItem();
        var everything = entry.RenderMessage() + string.Join("|", entry.Properties.Select(p => p.Key + "=" + p.Value));
        everything.ShouldNotContain("SECRET");
        everything.ShouldContain("visible");
    }

    [Fact]
    public async Task Long_messages_are_truncated_and_control_characters_removed()
    {
        await StartAsync(new() { ["Gateway:ClientLogs:MaxMessageLength"] = "100" });

        using var response = await Browser().PostAsync(Path, Json(
            $$"""[{"level":"error","source":"s","message":"line1\nFAKE {{new string('x', 500)}}"}]"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var message = Prop(_sink.Browser().ShouldHaveSingleItem(), "ClientMessage");
        message.Length.ShouldBeLessThanOrEqualTo(100);
        message.ShouldNotContain('\n');
    }

    [Fact]
    public async Task Over_limit_returns_429()
    {
        await StartAsync(new() { ["Gateway:ClientLogs:PermitPerMinute"] = "2" });
        var browser = Browser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await browser.PostAsync(Path, Json("""[{"level":"warn","source":"s","message":"m"}]"""), Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests]);
        _sink.Browser().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_json_is_400()
    {
        await StartAsync();

        using var response = await Browser().PostAsync(Path, Json("{not json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oversized_body_still_413()
    {
        await StartAsync();

        using var response = await Browser().PostAsync(Path, Json(new string('x', 70 * 1024)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _sink.Browser().ShouldBeEmpty();
    }
}
