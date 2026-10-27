using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Webhooks;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/test/webhook")]
public sealed class WebhookTestController : ControllerBase
{
    [SignedWebhook("supplier")]
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        return Ok(new { body = await reader.ReadToEndAsync(cancellationToken) });
    }

    // Not a webhook: it still needs a token, so the anonymous semantics stay on the webhook action only.
    [HttpPost("protected")]
    public IActionResult Protected() => Ok();
}

[Collection(GlobalStateCollection.Name)]
public sealed class SignedWebhookFilterTests
{
    private const string Secret = "supplier-secret-0123456789-0123456789";
    private const string Url = "/api/v1/test/webhook";
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Microsoft.AspNetCore.Builder.WebApplication> StartAsync(
        CapturingSink? sink = null, Dictionary<string, string?>? config = null, ManualTimeProvider? clock = null)
    {
        var settings = new Dictionary<string, string?> { ["Webhooks:Sources:supplier:Secret"] = Secret, ["Webhooks:MaxBodyBytes"] = "1024" };
        foreach (var (key, value) in config ?? [])
        {
            settings[key] = value;
        }

        return await TestHost.StartAsync(settings, sink, builder =>
        {
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(clock ?? new ManualTimeProvider(Start));
            builder.Services.AddProcureAiWebhooks(builder.Configuration);
        });
    }

    private static HttpRequestMessage Post(string body, string secret = Secret, DateTimeOffset? at = null, bool sign = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return sign ? WebhookTestSigner.Sign(request, secret, at ?? Start) : request;
    }

    private static HttpRequestMessage FailingRequest(string reason)
    {
        switch (reason)
        {
            case "missing":
                return Post("{}", sign: false);
            case "malformed":
                var request = Post("{}", sign: false);
                request.Headers.Add(WebhookSignature.HeaderName, "t=oops");
                return request;
            case "expired":
                return Post("{}", at: Start.AddSeconds(-301));
            default:
                return Post("{}", secret: "another-secret-0123456789-0123456789");
        }
    }

    [Fact]
    public async Task A_valid_signature_reaches_the_action_with_the_full_body()
    {
        await using var app = await StartAsync();
        const string body = "{ \"eventId\":\"e1\",   \"note\":\"ünïcode  and   spaces\" }";

        var response = await app.GetTestClient().SendAsync(Post(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("body").GetString().ShouldBe(body);
    }

    public static TheoryData<string> Failures => new() { "missing", "malformed", "expired", "bad_signature" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Every_failure_gives_the_same_401_and_one_counted_warning(string reason)
    {
        var sink = new CapturingSink();
        using var counter = new RejectionCounter();
        await using var app = await StartAsync(sink);
        var client = app.GetTestClient();

        var reference = await client.SendAsync(FailingRequest("bad_signature"), Ct);
        var before = counter.Count("supplier", reason);
        var response = await client.SendAsync(FailingRequest(reason), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        reference.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var left = JsonDocument.Parse(text);
        using var right = JsonDocument.Parse(await reference.Content.ReadAsStringAsync(Ct));
        left.RootElement.GetProperty("title").GetString().ShouldBe(right.RootElement.GetProperty("title").GetString());
        left.RootElement.TryGetProperty("detail", out _).ShouldBeFalse();
        text.ShouldNotContain(reason);
        counter.Count("supplier", reason).ShouldBe(before + 1);
        lock (sink.Events)
        {
            sink.Events.ShouldContain(e => e.MessageTemplate.Text == "Webhook rejected {Source} {Reason}"
                && e.Properties["Reason"].ToString() == $"\"{reason}\"" && e.Properties["Source"].ToString() == "\"supplier\"");
        }
    }

    [Fact]
    public async Task A_body_over_the_limit_gets_413_even_with_a_valid_signature()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(Post(new string('a', 2000)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_body_over_the_limit_without_a_content_length_gets_413()
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StreamContent(new MemoryStream(new byte[2000])) };
        request.Headers.TransferEncodingChunked = true;
        request.Content.Headers.ContentLength = null;

        var response = await app.GetTestClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task The_tolerance_follows_the_clock()
    {
        var clock = new ManualTimeProvider(Start);
        await using var app = await StartAsync(clock: clock);
        var client = app.GetTestClient();

        (await client.SendAsync(Post("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        (await client.SendAsync(Post("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Other_actions_still_need_a_token()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsync(Url + "/protected", new StringContent("{}"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_source_without_a_secret_stops_the_host_from_starting()
    {
        var failure = await Should.ThrowAsync<Exception>(() => TestHost.StartAsync(
            new Dictionary<string, string?>(), null, builder => builder.Services.AddProcureAiWebhooks(builder.Configuration)));

        failure.ToString().ShouldContain("supplier");
    }

    [Fact]
    public async Task A_short_secret_stops_the_host_from_starting()
    {
        var failure = await Should.ThrowAsync<Exception>(
            () => StartAsync(config: new() { ["Webhooks:Sources:supplier:Secret"] = "too-short" }));

        failure.ToString().ShouldContain("Webhooks:Sources:supplier:Secret");
    }

    private sealed class RejectionCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Source, string Reason)> _seen = new();

        public RejectionCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == WebhookMetrics.RejectedCounter && instrument.Meter.Name == WebhookMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");
                _seen.Enqueue((map["source"], map["reason"]));
            });
            _listener.Start();
        }

        public int Count(string source, string reason) => _seen.Count(s => s.Source == source && s.Reason == reason);

        public void Dispose() => _listener.Dispose();
    }
}
