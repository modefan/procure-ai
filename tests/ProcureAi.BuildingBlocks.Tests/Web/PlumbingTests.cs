using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Logging;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public class PlumbingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_live_returns_200()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/health/live", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_returns_200()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DomainException_becomes_422_problem_details()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/test/domain-error", Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)422);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("title").GetString().ShouldBe("Budget exceeded");
    }

    [Fact]
    public async Task Validation_failure_becomes_400_problem_details()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/test/validate", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Correlation_id_is_echoed()
    {
        await using var app = await TestHost.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test/ping");
        request.Headers.Add("X-Correlation-Id", "abc");
        var echoed = await client.SendAsync(request, Ct);
        var generated = await client.GetAsync("/api/v1/test/ping", Ct);

        echoed.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("abc");
        generated.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Correlation_id_is_on_error_responses()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/test/domain-error", Ct);

        response.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("\"/api/v1/test/ping\"");
    }

    [Fact]
    public async Task Serilog_level_comes_from_configuration()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(
            new() { ["Serilog:MinimumLevel:Default"] = "Warning" }, sink);

        (await app.GetTestClient().GetAsync("/api/v1/test/log", Ct)).EnsureSuccessStatusCode();

        var messages = sink.Events.Select(e => e.RenderMessage()).ToList();
        messages.ShouldContain("warning-event");
        messages.ShouldNotContain("information-event");
    }

    [Fact]
    public async Task Log_events_carry_the_service_name_and_correlation_id()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test/log");
        request.Headers.Add("X-Correlation-Id", "corr-42");
        (await app.GetTestClient().SendAsync(request, Ct)).EnsureSuccessStatusCode();

        var warning = sink.Events.Single(e => e.RenderMessage() == "warning-event");
        warning.Properties["Service"].ToString().ShouldBe("\"test-service\"");
        warning.Properties["CorrelationId"].ToString().ShouldBe("\"corr-42\"");
        warning.Properties.ShouldContainKey("TraceId");
    }

    [Fact]
    public async Task Invalid_options_fail_at_startup()
    {
        await Should.ThrowAsync<OptionsValidationException>(
            () => TestHost.StartAsync(new() { ["Caching:DefaultGetSeconds"] = "0" }));
    }

    [Fact]
    public async Task Rate_limiter_rejects_requests_over_the_limit()
    {
        await using var app = await TestHost.StartAsync(new()
        {
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60",
            ["RateLimiting:QueueLimit"] = "0",
        });
        var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.GetAsync("/api/v1/test/ping", Ct)).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task Health_probes_are_not_rate_limited()
    {
        await using var app = await TestHost.StartAsync(new()
        {
            ["RateLimiting:PermitLimit"] = "1",
            ["RateLimiting:WindowSeconds"] = "60",
        });
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Sensitive_header_values_are_masked_when_destructured()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "secret-key";
        context.Request.Headers["Authorization"] = "Bearer abc";
        context.Request.Headers["Cookie"] = "session=1";
        context.Request.Headers["Accept"] = "application/json";

        var sink = new CapturingSink();
        using var logger = new Serilog.LoggerConfiguration()
            .Destructure.With<HeaderMaskingPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        logger.Information("{@Headers}", context.Request.Headers);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain("secret-key");
        rendered.ShouldNotContain("Bearer abc");
        rendered.ShouldNotContain("session=1");
        rendered.ShouldContain("application/json");
        rendered.ShouldContain(HeaderMaskingPolicy.Mask);
    }
}
