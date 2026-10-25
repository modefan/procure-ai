using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.Contracts.Access;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>The Access client of every service: cached, coalesced, fail-safe, evicted by AccessChanged. Against a real stub Access over HTTP and Redis.</summary>
[Collection(RedisCollection.Name)]
public sealed class AccessClientTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly List<WebApplication> _hosts = [];
    private StubAccessServer _access = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _access = await StubAccessServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }

        await _access.DisposeAsync();
    }

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private async Task<WebApplication> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var config = _access.Settings;
        config["ConnectionStrings:Redis"] = redis.ConnectionString;
        config["AccessClient:RetryDelay"] = "00:00:00.050";
        foreach (var (key, value) in extra ?? [])
        {
            config[key] = value;
        }

        var host = await TestHost.StartAsync(config, configure: builder =>
        {
            builder.AddProcureAiCaching();
            builder.Services.AddProcureAiAccessClient(builder.Configuration, "test-service");
        });
        _hosts.Add(host);
        return host;
    }

    private static async Task<IReadOnlySet<string>> PermissionsAsync(
        WebApplication host, string tenant, string user = "ben", params string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IPermissionProvider>();
        return await provider.GetPermissionsAsync(new FakeUser(user, tenant, roles.Length == 0 ? ["requester"] : roles), Ct);
    }

    private static HttpRequestMessage Get(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Permissions_come_from_access_and_are_cached()
    {
        var host = await StartAsync();
        var tenant = NewTenant();
        _access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead] });

        var first = await PermissionsAsync(host, tenant);
        var second = await PermissionsAsync(host, tenant);

        first.ShouldBe([Permissions.RequestsRead], ignoreOrder: true);
        second.ShouldBe(first, ignoreOrder: true);
        var calls = _access.Calls.Where(c => c.Tenant == tenant).ToList();
        calls.Count.ShouldBe(1);
        calls[0].CallingService.ShouldBe("test-service");
        calls[0].HasBearer.ShouldBeTrue();
    }

    [Fact]
    public async Task The_order_of_the_roles_does_not_split_the_cache()
    {
        var host = await StartAsync();
        var tenant = NewTenant();

        await PermissionsAsync(host, tenant, "ben", "manager", "requester");
        await PermissionsAsync(host, tenant, "ben", "requester", "manager");

        _access.Calls.Count(c => c.Tenant == tenant).ShouldBe(1);
    }

    [Fact]
    public async Task Identical_concurrent_lookups_call_access_once()
    {
        var host = await StartAsync();
        var tenant = NewTenant();
        _access.Delay = TimeSpan.FromMilliseconds(500);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PermissionsAsync(host, tenant)));

        _access.Calls.Count(c => c.Tenant == tenant).ShouldBe(1);
        results.ShouldAllBe(r => r.Contains(Permissions.RequestsCreate));
    }

    [Fact]
    public async Task Access_down_uses_last_good_value()
    {
        var host = await StartAsync(new() { ["AccessClient:CacheDuration"] = "00:00:01" });
        var tenant = NewTenant();
        var good = await PermissionsAsync(host, tenant);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), Ct); // the entry is now stale

        _access.Down = true;
        var whileDown = await PermissionsAsync(host, tenant);

        whileDown.ShouldBe(good, ignoreOrder: true);
        whileDown.ShouldContain(Permissions.RequestsCreate);
        _access.Calls.Count(c => c.Tenant == tenant).ShouldBeGreaterThan(1, "Access was asked again and failed");
    }

    [Fact]
    public async Task Access_down_and_nothing_cached_denies_403_not_500()
    {
        var host = await StartAsync();
        var tenant = NewTenant();
        var client = host.GetTestClient();
        var token = TestAuth.CreateToken("ben", tenant, "requester");
        _access.Down = true;

        var denied = await client.SendAsync(Get("/api/v1/auth-test/me", token), Ct);

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Nothing was cached from the failure: the next request after Access is back is served at once.
        _access.Down = false;
        var recovered = await client.SendAsync(Get("/api/v1/auth-test/me", token), Ct);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Access_slower_than_the_timeout_denies_403_not_500()
    {
        var host = await StartAsync(new()
        {
            ["AccessClient:AttemptTimeout"] = "00:00:00.300",
            ["AccessClient:TotalTimeout"] = "00:00:01",
        });
        _access.Delay = TimeSpan.FromSeconds(3);

        var response = await host.GetTestClient().SendAsync(
            Get("/api/v1/auth-test/me", TestAuth.CreateToken("ben", NewTenant(), "requester")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AccessChanged_evicts_the_tenant_on_both_instances()
    {
        var a = await StartAsync();
        var b = await StartAsync();
        var tenant = NewTenant();
        var other = NewTenant();
        _access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead] });
        await PermissionsAsync(a, tenant);
        await PermissionsAsync(b, tenant);
        await PermissionsAsync(b, other);
        _access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead, Permissions.RequestsCreate] });
        (await PermissionsAsync(b, tenant)).ShouldNotContain(Permissions.RequestsCreate, "still cached");

        await AccessChangedHandler.Handle(
            new AccessChanged(tenant, "Matrix", null, DateTimeOffset.UtcNow), a.Services.GetRequiredService<ICacheInvalidator>());

        (await PermissionsAsync(a, tenant)).ShouldContain(Permissions.RequestsCreate);
        await EventuallyAsync(async () => (await PermissionsAsync(b, tenant)).Contains(Permissions.RequestsCreate));
        // Only the changed tenant was evicted.
        var callsForOther = _access.Calls.Count(c => c.Tenant == other);
        await PermissionsAsync(b, other);
        _access.Calls.Count(c => c.Tenant == other).ShouldBe(callsForOther);
    }

    [Fact]
    public async Task Admin_key_never_calls_access()
    {
        var host = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth-test/me");
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", NewTenant());

        var response = await host.GetTestClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _access.EffectiveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_without_a_tenant_has_no_permissions_and_asks_nobody()
    {
        var host = await StartAsync();
        using var scope = host.Services.CreateScope();

        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionProvider>()
            .GetPermissionsAsync(new FakeUser("ben", null, ["requester"]), Ct);

        permissions.ShouldBeEmpty();
        _access.EffectiveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_system_lookup_names_the_tenant_it_is_given()
    {
        var host = await StartAsync();
        var tenant = NewTenant();
        // The test host's caller accessor is the system caller: nobody, and so no tenant of its own.
        host.Services.GetRequiredService<ICallerContextAccessor>().Current.UserId.ShouldBe(SystemCaller.Id);

        var access = host.Services.GetRequiredService<IAccessDirectory>();
        var effective = await access.GetEffectiveAccessAsync("ben", ["requester"], tenant, Ct);

        effective.Permissions.ShouldContain(Permissions.RequestsCreate);
        var call = _access.Calls.Single(c => c.Tenant == tenant);
        call.OnBehalfOf.ShouldBeNull();
        call.CallingService.ShouldBe("test-service");
    }

    [Fact]
    public async Task Approvers_are_read_from_access_and_cached()
    {
        var host = await StartAsync();
        var tenant = NewTenant();
        var access = host.Services.GetRequiredService<IAccessDirectory>();

        var first = await access.GetApproversAsync("CC-100", tenant, Ct);
        var second = await access.GetApproversAsync("CC-100", tenant, Ct);

        first.CostCenterCode.ShouldBe("CC-100");
        first.Manager.ShouldNotBeNull().UserId.ShouldBe("mia");
        first.DepartmentHead.ShouldBeNull();
        second.ShouldBe(first);
        _access.Calls.Count(c => c.Tenant == tenant).ShouldBe(1);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within 10 s.");
            }

            await Task.Delay(100, Ct);
        }
    }

    private sealed class FakeUser(string userId, string? tenant, string[] roles) : ICurrentUser
    {
        public string UserId => userId;
        public string? TenantId => tenant;
        public IReadOnlySet<string> Roles { get; } = roles.ToHashSet();
        public bool IsAuthenticated => true;
        public bool IsAdminApiKey => false;
        public bool IsServiceAccount => false;
        public string? CallingService => null;
        public string? OnBehalfOfUser => null;
    }
}
