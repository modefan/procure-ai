using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;
using StackExchange.Redis;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The system status endpoint (plan 06 rulings 9 and 10): it merges the snapshots the instances write to Redis. The tests write snapshots of
/// services of their own (unique names and tenants), as the other services' publishers do, next to the real ones of the Notifications instances.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class SystemStatusTests(NotificationsApp app)
{
    private const string Path = "/api/v1/notifications/system-status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    [Fact]
    public async Task Status_lists_service_without_snapshot_as_down()
    {
        var tenant = Unique("t");
        var missing = Unique("gone") + "-api";
        var running = Unique("up") + "-api";
        await WriteAsync(Snapshot(running, "1", health: "Degraded"));
        await using var instance = new NotificationsApiFactory(app, builder =>
        {
            builder.UseSetting("Status:ExpectedServices:0", missing);
            builder.UseSetting("Status:ExpectedServices:1", running);
        });

        var status = await GetAsync(instance, Admin(tenant));

        var services = status.GetProperty("services").EnumerateArray().ToList();
        services[0].GetProperty("name").GetString().ShouldBe(missing, "the expected services come first, in their order");
        services[0].GetProperty("health").GetString().ShouldBe("Down");
        services[0].GetProperty("instances").GetInt32().ShouldBe(0);
        services[1].GetProperty("name").GetString().ShouldBe(running);
        services[1].GetProperty("health").GetString().ShouldBe("Degraded");
        services[1].GetProperty("instances").GetInt32().ShouldBe(1);
        // The instances' own publishers: Notifications itself is listed with its real snapshot. (The test hosts share one process, and so one
        // instance ID: ServiceIdentity.InstanceId names the process.)
        var notifications = services.Single(s => s.GetProperty("name").GetString() == "notifications-api");
        notifications.GetProperty("instances").GetInt32().ShouldBe(1);
        notifications.GetProperty("health").GetString().ShouldBe("Healthy");
        status.GetProperty("counters").GetProperty(StatusCounters.CoalescedToday).GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Two_instances_counted_and_worst_breaker_wins()
    {
        var tenant = Unique("t");
        var service = Unique("svc") + "-api";
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(20);
        await WriteAsync(Snapshot(service, "a", breakers: [new BreakerDto("Supplier API", CircuitState.Closed, null), new BreakerDto("ERP", CircuitState.HalfOpen, null)],
            messages: new WolverineCountsDto(1, 2, 3, 4, 5), at: DateTimeOffset.UtcNow.AddSeconds(-5)));
        await WriteAsync(Snapshot(service, "b", breakers: [new BreakerDto("Supplier API", CircuitState.Open, retryAt), new BreakerDto("ERP", CircuitState.Closed, null)],
            messages: new WolverineCountsDto(10, 20, 30, 40, 50), at: DateTimeOffset.UtcNow));

        var status = await GetAsync(app.A, Admin(tenant));

        var svc = status.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == service);
        svc.GetProperty("instances").GetInt32().ShouldBe(2);
        svc.GetProperty("health").GetString().ShouldBe("Healthy");
        var breakers = svc.GetProperty("breakers").EnumerateArray().ToDictionary(b => b.GetProperty("name").GetString()!);
        breakers["Supplier API"].GetProperty("state").GetString().ShouldBe("Open", "one instance's open circuit is the service's");
        breakers["Supplier API"].GetProperty("retryAt").GetDateTimeOffset().ShouldBe(retryAt, TimeSpan.FromMilliseconds(1));
        breakers["ERP"].GetProperty("state").GetString().ShouldBe("HalfOpen");
        svc.GetProperty("messages").GetProperty("incoming").GetInt64().ShouldBe(10, "the newest snapshot's counts");
        svc.GetProperty("messages").GetProperty("brokerDeadLetters").GetInt64().ShouldBe(50);
    }

    [Fact]
    public async Task Status_counters_for_user_are_tenant_scoped()
    {
        var acme = Unique("acme");
        var globex = Unique("globex");
        var service = Unique("svc") + "-api";
        var system = Unique("sys").Replace('-', '_');
        var now = DateTimeOffset.UtcNow;
        var section = new StatusSection(
            "business",
            new Dictionary<string, long>
            {
                [StatusCounters.Key(acme, StatusCounters.SagasRunning)] = 3,
                [StatusCounters.Key(globex, StatusCounters.SagasRunning)] = 7,
                [StatusCounters.Key(globex, StatusCounters.OutsideAppAlertsOpen)] = 9,
                [StatusCounters.Key(null, system)] = 11,
            },
            [
                new StatusItem("outside-app-change", "Acme row changed in SQL", acme, now, "/app/admin/data-changes?outside=true&table=a"),
                new StatusItem("outside-app-change", "Globex row changed in SQL", globex, now, "/app/admin/data-changes?outside=true&table=g"),
                new StatusItem("outside-app-change", "A table without a tenant", null, now.AddSeconds(-1), null),
            ]);
        // Two instances report the same numbers (one database): they are counted once.
        await WriteAsync(Snapshot(service, "a", sections: [section]));
        await WriteAsync(Snapshot(service, "b", sections: [section]));

        var status = await GetAsync(app.B, Admin(acme));

        var counters = status.GetProperty("counters");
        counters.GetProperty(StatusCounters.SagasRunning).GetInt64().ShouldBe(3, "only the caller's tenant, once per service");
        counters.TryGetProperty(StatusCounters.OutsideAppAlertsOpen, out _).ShouldBeFalse("another tenant's counter is not even named");
        counters.GetProperty(system).GetInt64().ShouldBe(11, "system-wide values are for everyone");
        var alerts = status.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("text").GetString()).ToList();
        alerts.ShouldContain("Acme row changed in SQL");
        alerts.ShouldContain("A table without a tenant");
        alerts.ShouldNotContain("Globex row changed in SQL");
        alerts.Count(a => a == "Acme row changed in SQL").ShouldBe(1);
        JsonSerializer.Serialize(status).ShouldNotContain(globex, Case.Insensitive, "nothing of Globex reaches an Acme admin");

        var asGlobex = await GetAsync(app.B, Admin(globex));
        asGlobex.GetProperty("counters").GetProperty(StatusCounters.SagasRunning).GetInt64().ShouldBe(7);
        asGlobex.GetProperty("counters").GetProperty(StatusCounters.OutsideAppAlertsOpen).GetInt64().ShouldBe(9);
    }

    [Fact]
    public async Task Status_without_admin_system_is_403()
    {
        var tenant = Unique("t");
        using var client = app.A.CreateClient();
        foreach (var role in new[] { "requester", "manager", "finance" })
        {
            using var response = await client.SendAsync(Request(TestAuth.CreateToken("someone", tenant, role)), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, role);
        }

        using var anonymous = await client.GetAsync(Path, Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Supplier_chaos_from_purchasings_snapshot_is_shown()
    {
        var restoresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        // The newest purchasing-api snapshot carries the switch; written last, so it is the newest of the collection's.
        await WriteAsync(Snapshot("purchasing-api", Unique("p"), sections: [SupplierChaosSection.From(new ChaosDto(ChaosMode.Down, restoresAt))]));

        var status = await GetAsync(app.A, Admin(Unique("t")));

        var chaos = status.GetProperty("supplierChaos");
        chaos.GetProperty("mode").GetString().ShouldBe("Down");
        chaos.GetProperty("restoresAt").GetDateTimeOffset().ShouldBe(restoresAt, TimeSpan.FromMilliseconds(1));
    }

    private static string Admin(string tenant) => TestAuth.CreateToken("ada", tenant, "admin");

    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<JsonElement> GetAsync(NotificationsApiFactory instance, string token)
    {
        using var client = instance.CreateClient();
        using var response = await client.SendAsync(Request(token), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
    }

    private static StatusSnapshot Snapshot(
        string service, string instance, string health = "Healthy", BreakerDto[]? breakers = null, WolverineCountsDto? messages = null,
        StatusSection[]? sections = null, DateTimeOffset? at = null) =>
        new(service, $"{service}/{instance}", at ?? DateTimeOffset.UtcNow, health, breakers ?? [], messages ?? WolverineCountsDto.None, sections ?? []);

    /// <summary>Writes a snapshot the way <c>StatusPublisher</c> does (key, TTL, index).</summary>
    private async Task WriteAsync(StatusSnapshot snapshot)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(app.Redis.ConnectionString);
        var db = redis.GetDatabase();
        var key = StatusKeys.Snapshot(snapshot.Service, snapshot.InstanceId);
        await db.StringSetAsync(key, JsonSerializer.Serialize(snapshot, StatusKeys.Json), TimeSpan.FromSeconds(30));
        await db.SortedSetAddAsync(StatusKeys.Index, key, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
    }
}
