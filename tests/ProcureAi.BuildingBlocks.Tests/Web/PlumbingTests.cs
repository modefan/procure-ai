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
    public async Task Log_events_carry_tenant_and_user_from_claims()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test/whoami");
        request.Headers.Add("X-Test-Tenant", "acme");
        request.Headers.Add("X-Test-User", "user-7");
        (await app.GetTestClient().SendAsync(request, Ct)).EnsureSuccessStatusCode();

        var warning = sink.Events.Single(e => e.RenderMessage() == "whoami-event");
        warning.Properties["TenantId"].ToString().ShouldBe("\"acme\"");
        warning.Properties["UserId"].ToString().ShouldBe("\"user-7\"");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad/chars?<script>")]
    [InlineData("line\u0001")]
    public async Task Unsafe_correlation_id_is_replaced(string supplied)
    {
        await using var app = await TestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test/ping");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);
        var response = await app.GetTestClient().SendAsync(request, Ct);

        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        id.ShouldNotBe(supplied);
        id.ShouldMatch("^[A-Za-z0-9._-]+$");
    }

    [Fact]
    public async Task Oversized_correlation_id_is_replaced()
    {
        await using var app = await TestHost.StartAsync();
        var supplied = new string('a', 129);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test/ping");
        request.Headers.Add("X-Correlation-Id", supplied);
        var response = await app.GetTestClient().SendAsync(request, Ct);

        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        id.ShouldNotBe(supplied);
        id.ShouldMatch("^[A-Za-z0-9._-]{1,128}$");
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_404()
    {
        await using var app = await TestHost.StartAsync();

        // Anonymous callers get 401 everywhere (fallback policy); an authenticated caller sees the 404.
        var anonymous = await app.GetTestClient().GetAsync("/nope", Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/nope");
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        var response = await app.GetTestClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Unhandled_exception_returns_generic_500_without_details()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/test/crash", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("secret-internal-detail");
        body.ShouldNotContain("InvalidOperationException");
        body.ShouldNotContain(" at ");
    }

    [Fact]
    public async Task Handled_domain_exception_is_not_logged_as_unhandled()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);

        await app.GetTestClient().GetAsync("/api/v1/test/domain-error", Ct);

        sink.Events.ShouldNotContain(e => e.Level >= Serilog.Events.LogEventLevel.Error);
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
        context.Request.Headers["Set-Cookie"] = "s=2";
        context.Request.Headers["Proxy-Authorization"] = "Basic xyz";
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
        rendered.ShouldNotContain("s=2");
        rendered.ShouldNotContain("Basic xyz");
        rendered.ShouldContain("application/json");
        rendered.ShouldContain(HeaderMaskingPolicy.Mask);
    }
}
