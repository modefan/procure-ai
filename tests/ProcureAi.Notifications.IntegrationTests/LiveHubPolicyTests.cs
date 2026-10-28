using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.Notifications.Api.Hubs;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The <c>live-hub</c> policy on its own (no containers). SignalR evaluates it with an <see cref="HttpContext"/> for negotiate and
/// connect, and with a <see cref="HubInvocationContext"/> for a hub method with its own <c>[Authorize]</c> (Task 1b's Watch and
/// Heartbeat): both must read the caller from the connection's request.
/// </summary>
public sealed class LiveHubPolicyTests
{
    private readonly ServiceProvider _services;

    public LiveHubPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new AuthOptions { Authority = "https://issuer.test", Audience = "test" }));
        services.AddAuthorizationBuilder().AddLiveHubPolicy();
        _services = services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(string? sub, string? tenant)
    {
        var claims = new List<Claim>();
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        if (tenant is not null)
        {
            claims.Add(new Claim("tenant", tenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "sub", "roles"));
    }

    private HttpContext Request(ClaimsPrincipal user) => new DefaultHttpContext { RequestServices = _services, User = user };

    private HubInvocationContext Invocation(ClaimsPrincipal user)
    {
        var caller = new TestHubCallerContext(Request(user));
        // Only the policy runs here: the hub's presence service is never reached.
        var hub = new LiveHub(_services.GetRequiredService<IOptions<AuthOptions>>(), presence: null!);
        return new HubInvocationContext(caller, _services, hub, typeof(LiveHub).GetMethod(nameof(LiveHub.Heartbeat))!, []);
    }

    private async Task<bool> AllowedAsync(ClaimsPrincipal user, object resource) =>
        (await _services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource, LiveHubPolicy.Name)).Succeeded;

    [Fact]
    public async Task A_tenant_user_is_allowed_on_negotiate_and_on_hub_methods()
    {
        var user = User("tom", "acme");

        (await AllowedAsync(user, Request(user))).ShouldBeTrue("negotiate / connect (HttpContext)");
        (await AllowedAsync(user, Invocation(user))).ShouldBeTrue("hub method (HubInvocationContext)");
    }

    [Theory]
    [InlineData(null, "acme")]
    [InlineData("", "acme")]
    [InlineData("tom", null)]
    public async Task A_caller_without_user_id_or_tenant_is_refused(string? sub, string? tenant)
    {
        var user = User(sub, tenant);

        (await AllowedAsync(user, Request(user))).ShouldBeFalse();
        (await AllowedAsync(user, Invocation(user))).ShouldBeFalse();
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly FeatureCollection _features = new();

        public TestHubCallerContext(HttpContext http)
        {
            User = http.User;
            _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
        }

        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => User?.FindFirst("sub")?.Value;

        public override ClaimsPrincipal? User { get; }

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class HttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
