using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Http;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public sealed class ServiceTokenHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeCaller(string userId, string? tenant) : ICallerContext, ICallerContextAccessor
    {
        public string UserId => userId;
        public string ActorId => userId;
        public string? TenantId => tenant;
        public string? CallingService => null;
        public CallerKind Kind => CallerKind.User;
        public ICallerContext Current => this;
    }

    /// <summary>Plays Keycloak: counts token requests, can be slow or failing, and issues <c>token-N</c>.</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler, IHttpClientFactory
    {
        private int _requests;

        public int Requests => _requests;
        public TaskCompletionSource? Gate { get; set; }
        public bool Fail { get; set; }
        public string? LastBody { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _requests);
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"token-{{number}}","expires_in":300,"token_type":"Bearer"}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Downstream : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen)
            {
                Seen.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static readonly InternalClientOptions Options = new()
    {
        TokenUrl = "http://keycloak.test/realms/procure/protocol/openid-connect/token",
        ClientId = "procure-internal",
        ClientSecret = "a-test-secret",
        RefreshSkew = TimeSpan.FromSeconds(30),
    };

    private static (HttpClient Client, Downstream Downstream, FakeTokenEndpoint Keycloak, ManualTimeProvider Clock) Create(
        string userId = "tom", string? tenant = "acme")
    {
        var keycloak = new FakeTokenEndpoint();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
        var provider = new ServiceTokenProvider(keycloak, Microsoft.Extensions.Options.Options.Create(Options), clock);
        var downstream = new Downstream();
        var handler = new ServiceTokenHandler(provider, new FakeCaller(userId, tenant), new ServiceIdentity("approvals-api"))
        {
            InnerHandler = downstream,
        };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://budgets") }, downstream, keycloak, clock);
    }

    [Fact]
    public async Task Caches_token_until_expiry_minus_skew()
    {
        var (client, downstream, keycloak, clock) = Create();

        await client.GetAsync("/a", Ct);
        clock.Advance(TimeSpan.FromSeconds(260)); // 300 s token, 30 s skew: still good at 260 s
        await client.GetAsync("/b", Ct);
        keycloak.Requests.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(15)); // 275 s: past 270 s (expiry minus skew)
        await client.GetAsync("/c", Ct);

        keycloak.Requests.ShouldBe(2);
        downstream.Seen.Select(r => r.Headers.Authorization!.Parameter).ShouldBe(["token-1", "token-1", "token-2"]);
        var form = keycloak.LastBody.ShouldNotBeNull();
        form.ShouldContain("grant_type=client_credentials");
        form.ShouldContain("client_id=procure-internal");
    }

    [Fact]
    public async Task Sends_calling_service_and_on_behalf_of_headers()
    {
        var (client, downstream, _, _) = Create("tom", "acme");

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.Single();
        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.GetValues("X-Calling-Service").ShouldBe(["approvals-api"]);
        request.Headers.GetValues("X-On-Behalf-Of-User").ShouldBe(["tom"]);
        request.Headers.GetValues("X-Tenant-Id").ShouldBe(["acme"]);
    }

    [Fact]
    public async Task Sends_only_calling_service_when_acting_as_the_system()
    {
        var (client, downstream, _, _) = Create(SystemCaller.Id, null);

        await client.GetAsync("/a", Ct);

        var request = downstream.Seen.Single();
        request.Headers.GetValues("X-Calling-Service").ShouldBe(["approvals-api"]);
        request.Headers.Contains("X-On-Behalf-Of-User").ShouldBeFalse();
        request.Headers.Contains("X-Tenant-Id").ShouldBeFalse();
    }

    [Fact]
    public async Task A_request_can_name_the_user_it_is_for_when_the_caller_is_the_system()
    {
        // The gateway has no caller context of its own (system), but registers the signed-in user on their behalf.
        var (client, downstream, _, _) = Create(SystemCaller.Id, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/register");
        request.Options.Set(ServiceTokenHandler.OnBehalfOfUserOption, "new-user");
        request.Options.Set(ServiceTokenHandler.TenantOption, "demo");
        request.Headers.Add("X-On-Behalf-Of-User", "forged");

        await client.SendAsync(request, Ct);

        var sent = downstream.Seen.Single();
        sent.Headers.GetValues("X-On-Behalf-Of-User").ShouldBe(["new-user"]);
        sent.Headers.GetValues("X-Tenant-Id").ShouldBe(["demo"]);
    }

    [Fact]
    public async Task Concurrent_calls_request_one_token()
    {
        var (client, downstream, keycloak, _) = Create();
        keycloak.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, 10).Select(i => client.GetAsync($"/{i}", Ct)).ToArray();
        await Task.Delay(200, Ct); // every caller is now waiting for the one token request
        keycloak.Gate.SetResult();
        await Task.WhenAll(calls);

        keycloak.Requests.ShouldBe(1);
        downstream.Seen.Count.ShouldBe(10);
        downstream.Seen.ShouldAllBe(r => r.Headers.Authorization!.Parameter == "token-1");
    }

    [Fact]
    public async Task A_failed_token_request_is_not_cached()
    {
        var (client, downstream, keycloak, _) = Create();
        keycloak.Fail = true;

        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("/a", Ct));
        keycloak.Fail = false;
        var response = await client.GetAsync("/b", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        keycloak.Requests.ShouldBe(2);
        downstream.Seen.Single().Headers.Authorization!.Parameter.ShouldBe("token-2");
    }

    [Fact]
    public void Options_without_token_url_client_id_or_secret_are_invalid()
    {
        var options = new InternalClientOptions();
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeFalse();

        results.Count.ShouldBe(3);
    }
}
