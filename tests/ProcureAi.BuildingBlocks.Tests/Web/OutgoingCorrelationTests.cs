using System.Net;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/test-outgoing")]
public sealed class OutgoingCallController(IHttpClientFactory clients) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Call()
    {
        // Created from the factory, like any typed client: the defaults (the correlation handler) apply.
        using var response = await clients.CreateClient("probe").GetAsync("http://probe.invalid/ping", HttpContext.RequestAborted);
        return StatusCode((int)response.StatusCode);
    }
}

[Collection(GlobalStateCollection.Name)]
public sealed class OutgoingCorrelationTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task Outgoing_calls_carry_the_correlation_id_of_the_current_request()
    {
        var capture = new CapturingHandler();
        await using var app = await TestHost.StartAsync(configure: builder =>
            builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => capture));
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/test-outgoing");
        request.Headers.Add("X-Correlation-Id", "corr-123");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        capture.Last!.Headers.GetValues("X-Correlation-Id").ShouldBe(["corr-123"]);
    }
}
