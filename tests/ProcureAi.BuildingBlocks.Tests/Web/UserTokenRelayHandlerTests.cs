using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>The typed client the production setup would create; only exposes its <see cref="HttpClient"/>.</summary>
    private sealed class Probe(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    /// <summary>Plays service discovery: a default handler outside the client's own that rewrites the address.</summary>
    private sealed class Rewriter : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Host = "10.1.2.3", Port = 5123 }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    // Built through the production registration, so the tests cover how the handler is wired too.
    private static (HttpClient Client, Downstream Downstream) Create(HttpContext? context, Action<IServiceCollection>? configure = null)
    {
        var downstream = new Downstream();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new Accessor(context));
        services.AddSingleton(new ServiceIdentity("assistant-api"));
        configure?.Invoke(services);
        services.AddProcureAiUserClient<Probe, Probe>("requests-api").ConfigurePrimaryHttpMessageHandler(() => downstream);
        return (services.BuildServiceProvider().GetRequiredService<Probe>().Client, downstream);
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
        var (client, downstream) = Create(Incoming(
            ("Authorization", "Bearer user-token"), ("X-Tenant-Id", "spoofed"), ("X-On-Behalf-Of-User", "spoofed")));

        await client.GetAsync("/a", Ct);

        // A bearer token carries its own tenant and user; the headers only the admin key may use are not forwarded.
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
    public async Task Bearer_token_together_with_api_key_is_refused()
    {
        // Two credentials: which one the callee would honour is a guess, so nothing is sent.
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token"), ("X-Api-Key", "the-key"), ("X-Tenant-Id", "acme")));

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
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", "preset-tenant");
        request.Headers.TryAddWithoutValidation("X-On-Behalf-Of-User", "preset-user");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "preset-correlation");

        await client.SendAsync(request, Ct);

        var seen = downstream.Seen.ShouldNotBeNull();
        seen.Headers.GetValues("Authorization").ShouldBe(["Bearer user-token"]);
        seen.Headers.Contains("X-Api-Key").ShouldBeFalse();
        seen.Headers.GetValues("X-Calling-Service").ShouldBe(["assistant-api"]);
        seen.Headers.Contains("X-Tenant-Id").ShouldBeFalse();
        seen.Headers.Contains("X-On-Behalf-Of-User").ShouldBeFalse();
        seen.Headers.GetValues("X-Correlation-Id").ShouldBe(["corr-123"]);
    }

    [Theory]
    [InlineData("//evil.example/a")]
    [InlineData("http://evil.example/a")]
    [InlineData("https://requests-api/a")]
    [InlineData("http://requests-api:8080/a")]
    [InlineData("ftp://requests-api/a")]
    public async Task Refuses_to_send_credentials_to_another_host(string target)
    {
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token")));

        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(target, Ct));
        downstream.Seen.ShouldBeNull();
    }

    [Fact]
    public async Task Sends_to_the_configured_host_in_any_letter_case()
    {
        var (client, downstream) = Create(Incoming(("Authorization", "Bearer user-token")));

        await client.GetAsync("http://REQUESTS-API/a", Ct);

        downstream.Seen.ShouldNotBeNull();
    }

    [Fact]
    public async Task Works_behind_a_handler_that_rewrites_the_address_like_service_discovery()
    {
        // Service discovery is a default handler outside the client's own: the check must see the address as the caller wrote it.
        var (client, downstream) = Create(
            Incoming(("Authorization", "Bearer user-token")),
            services => services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(() => new Rewriter())));

        await client.GetAsync("/a", Ct);

        var seen = downstream.Seen.ShouldNotBeNull();
        seen.RequestUri!.Host.ShouldBe("10.1.2.3");
        seen.Headers.Authorization!.ToString().ShouldBe("Bearer user-token");
    }
}
