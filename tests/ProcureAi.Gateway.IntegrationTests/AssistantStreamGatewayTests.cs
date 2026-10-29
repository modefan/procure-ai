using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Yarp.ReverseProxy.Configuration;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The Assistant's answer stream through the gateway (plan 05 Task 5a, ruling 6): the session cookie authenticates the POST like any API
/// call (anti-forgery header, the session's access token forwarded), and the server-sent events reach the browser as the service writes
/// them, not buffered until the end. The route's cluster allows streams of up to 5 minutes without data.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class AssistantStreamGatewayTests(KeycloakFixture keycloak, RedisFixture redis)
{
    private const string Ask = "/api/v1/assistant/conversations/0191a3c4-0000-7000-8000-000000000001/messages";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assistant_stream_is_not_buffered()
    {
        // A real Kestrel listener: the in-memory test server would not show buffering by a real server.
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis, kestrel: true);
        using var browser = gateway.NewBrowser();
        using (var driver = new LoginDriver(keycloak))
        {
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var xsrf = await XsrfTokenAsync(browser);
        using var request = new HttpRequestMessage(HttpMethod.Post, Ask) { Content = JsonContent.Create(new { text = "Hello?", mode = "new" }) };
        request.Headers.Add("X-XSRF-TOKEN", xsrf);

        using var response = await browser.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        var events = new List<(string Line, long At)>();
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                events.Add((line, Stopwatch.GetTimestamp()));
            }
        }

        events.Select(e => e.Line).ShouldBe([.. Enumerable.Repeat("event: delta", StubDownstream.StreamDeltas), "event: done"]);
        var finished = gateway.Stub.StreamFinishedAt;
        finished.ShouldBeGreaterThan(0);
        // The first delta arrived while the service was still writing (it pauses 200 ms between events, 1 s in all).
        var lead = Stopwatch.GetElapsedTime(events[0].At, finished);
        lead.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500), "the first event must reach the browser long before the stream ends");

        var seen = gateway.Stub.Seen.Single(s => s.Path == Ask);
        seen.Method.ShouldBe("POST");
        var token = seen.Header("Authorization");
        token.ShouldNotBeNull().ShouldStartWith("Bearer ");
        new JsonWebTokenHandler().ReadJsonWebToken(token["Bearer ".Length..]).GetClaim("tenant").Value.ShouldBe("acme");
    }

    [Fact]
    public async Task Assistant_cluster_allows_long_streams_unbuffered()
    {
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);

        var config = gateway.App.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

        var cluster = config.Clusters.Single(c => c.ClusterId == "assistant");
        cluster.HttpRequest.ShouldNotBeNull();
        cluster.HttpRequest.ActivityTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        cluster.HttpRequest.AllowResponseBuffering.ShouldBe(false);
        config.Routes.Single(r => r.RouteId == "assistant-api").ClusterId.ShouldBe("assistant");
    }

    /// <summary>What the Angular app does: ask /bff/user, which hands out the XSRF token as a cookie.</summary>
    private static async Task<string> XsrfTokenAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/user", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }
}
