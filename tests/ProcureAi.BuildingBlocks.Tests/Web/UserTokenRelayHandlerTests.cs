using System.Net;
using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Correlation;
using ProcureAi.BuildingBlocks.Web.Http;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public sealed class UserTokenRelayHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Accessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class Downstream : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static (HttpClient Client, Downstream Downstream) Create(HttpContext? context)
    {
        var downstream = new Downstream();
        var handler = new UserTokenRelayHandler(new Accessor(context), new ServiceIdentity("assistant-api")) { InnerHandler = downstream };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://requests-api") }, downstream);
    }

    private static DefaultHttpContext Incoming(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        context.Items[CorrelationIdMiddleware.ItemKey] = "corr-123";
        return context;
    }

    [Fact]
    public async Task Relays_bearer_token_of_incoming_request()
    {
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token"), ("X-Tenant-Id", "acme")));

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.ShouldNotBeNull();
        request.Headers.Authorization!.ToString().ShouldBe("Bearer user-token");
        request.Headers.Contains("X-Api-Key").ShouldBeFalse();
        request.Headers.Contains("X-Tenant-Id").ShouldBeFalse();
        request.Headers.Contains("X-On-Behalf-Of-User").ShouldBeFalse();
    }

    [Fact]
    public async Task Relays_admin_key_with_tenant_and_on_behalf_headers()
    {
        var (client, downstream) = Create(Incoming(("X-Api-Key", "the-key"), ("X-Tenant-Id", "acme"), ("X-On-Behalf-Of-User", "tom")));

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.ShouldNotBeNull();
        request.Headers.Authorization.ShouldBeNull();
        request.Headers.GetValues("X-Api-Key").ShouldBe(["the-key"]);
        request.Headers.GetValues("X-Tenant-Id").ShouldBe(["acme"]);
        request.Headers.GetValues("X-On-Behalf-Of-User").ShouldBe(["tom"]);
    }

    [Fact]
    public async Task Admin_key_without_tenant_sends_no_tenant_header()
    {
        var (client, downstream) = Create(Incoming(("X-Api-Key", "the-key")));

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.ShouldNotBeNull();
        request.Headers.Contains("X-Tenant-Id").ShouldBeFalse();
        request.Headers.Contains("X-On-Behalf-Of-User").ShouldBeFalse();
    }

    [Fact]
    public async Task Sets_calling_service_and_correlation_id()
    {
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token")));

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.ShouldNotBeNull();
        request.Headers.GetValues("X-Calling-Service").ShouldBe(["assistant-api"]);
        request.Headers.GetValues("X-Correlation-Id").ShouldBe(["corr-123"]);
    }

    [Fact]
    public async Task Without_http_context_throws_UserCredentialMissingException()
    {
        var (client, downstream) = Create(null);

        await Should.ThrowAsync<UserCredentialMissingException>(() => client.GetAsync("/a", Ct));
        downstream.Seen.ShouldBeNull();
    }

    [Fact]
    public async Task Without_credential_throws_UserCredentialMissingException()
    {
        var (client, downstream) = Create(Incoming(("X-Tenant-Id", "acme")));

        await Should.ThrowAsync<UserCredentialMissingException>(() => client.GetAsync("/a", Ct));
        downstream.Seen.ShouldBeNull();
    }

    [Fact]
    public async Task Strips_preset_authorization_header()
    {
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/a");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer service-token");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "preset-key");
        request.Headers.TryAddWithoutValidation("X-Calling-Service", "someone-else");

        await client.SendAsync(request, Ct);

        var seen = downstream.Seen.ShouldNotBeNull();
        seen.Headers.GetValues("Authorization").ShouldBe(["Bearer user-token"]);
        seen.Headers.Contains("X-Api-Key").ShouldBeFalse();
        seen.Headers.GetValues("X-Calling-Service").ShouldBe(["assistant-api"]);
    }
}
