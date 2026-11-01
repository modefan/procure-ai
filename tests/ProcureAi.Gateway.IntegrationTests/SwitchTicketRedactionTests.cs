using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Contracts;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// Plan 06 carry-over 18 (plan 07 Task 0a): the single-use ticket of a code-flow tenant switch (<c>/bff/switch-tenant/challenge?ticket=…</c>)
/// never reaches the request logs or the traces. Query values show as <c>***</c> in the hosting log's <c>QueryString</c> and in the server
/// span's <c>url.query</c>. Real Keycloak and Redis, stub Access.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class SwitchTicketRedactionTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Thandi = "thandi@acme.test";

    private readonly CollectingSink _sink = new();
    private readonly List<IDisposable> _disposables = [];
    private GatewayHost _gateway = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _gateway = await GatewayHost.StartAsync(keycloak, redis, new()
        {
            // The request log (Request starting / finished) is off by default (Microsoft.AspNetCore: Warning); a deployment may switch it on.
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Information",
        }, configureServices: services => services.AddSingleton<ILogEventSink>(_sink));
        _gateway.Access.SetUserTenants(DemoUsers.Thandi, ("acme", "Acme Corp"), ("globex", "Globex"));
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

    private static string Xsrf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    [Fact]
    public async Task Switch_ticket_is_redacted_in_logs_and_traces()
    {
        var (browser, _) = _gateway.NewBrowserWithCookies();
        _disposables.Add(browser);
        var driver = new LoginDriver(keycloak);
        _disposables.Add(driver);
        await driver.LoginAsync(browser, Thandi, organization: "acme");

        using var user = await browser.GetAsync("/bff/user", Ct);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/bff/switch-tenant")
        {
            Content = JsonContent.Create(new { tenant = "globex", returnUrl = "/app/dashboard" }),
        };
        post.Headers.Add("X-XSRF-TOKEN", Xsrf(user));
        using var switched = await browser.SendAsync(post, Ct);
        switched.StatusCode.ShouldBe(HttpStatusCode.OK, await switched.Content.ReadAsStringAsync(Ct));
        var navigateTo = (await switched.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("navigateTo").GetString()!;
        navigateTo.ShouldStartWith("/bff/switch-tenant/challenge?ticket=");
        var ticket = Uri.UnescapeDataString(navigateTo["/bff/switch-tenant/challenge?ticket=".Length..]);

        // The challenge request in a trace of its own, so its server span is found among the other tests' spans.
        var traceId = ActivityTraceId.CreateRandom();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var challenge = new HttpRequestMessage(HttpMethod.Get, navigateTo);
        challenge.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var redirected = await browser.SendAsync(challenge, Ct);
        LoginDriver.IsRedirect(redirected.StatusCode).ShouldBeTrue("the ticket was valid: the challenge started");

        // The server span stops (and Request finished is logged) when the request ends, which can be just after the test server handed
        // the response to the client.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (SpanCount(spans) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        // Logs: the request log shows the redacted query; no event anywhere holds the ticket.
        var events = _sink.All();
        events.ShouldContain(e => IsChallengeRequestLog(e), "the request log (Request starting) names the parameter only");
        events.ShouldNotContain(e =>
            e.RenderMessage(null).Contains(ticket) || e.Properties.Values.Any(v => v.ToString().Contains(ticket)));

        // Traces: the server span names the parameter, never its value.
        Activity span;
        lock (spans)
        {
            span = spans.ShouldHaveSingleItem();
        }

        span.GetTagItem("url.query").ShouldBe("?ticket=***", "the instrumentation's own format (with the ?), our mask");
        span.TagObjects.ShouldNotContain(t => t.Value != null && t.Value.ToString()!.Contains(ticket), "no tag holds the ticket");
        span.DisplayName.ShouldNotContain(ticket);
    }

    private static int SpanCount(List<Activity> spans)
    {
        lock (spans)
        {
            return spans.Count;
        }
    }

    private static bool IsChallengeRequestLog(LogEvent e) =>
        e.Properties.TryGetValue("Path", out var path) && path is ScalarValue { Value: "/bff/switch-tenant/challenge" }
        && e.Properties.TryGetValue("QueryString", out var query) && query is ScalarValue { Value: "?ticket=***" };
}
