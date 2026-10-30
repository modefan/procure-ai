using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>
/// "Break the supplier API" (plan 06 ruling 11): <c>PUT api/v1/purchasing/admin/supplier-chaos</c> sets the simulator's supplier mode through
/// the simulator's chaos switch with the simulators' key, keeps everything else of the simulator's configuration, and reports when it restores
/// itself. Needs <c>admin.system</c>.
/// </summary>
[Collection(PurchasingCollection.Name)]
public sealed class SupplierChaosTests(PurchasingApp app)
{
    private const string Path = "/api/v1/purchasing/admin/supplier-chaos";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Supplier_chaos_put_sets_simulator_mode()
    {
        var tenant = PurchasingApp.NewTenant();
        var admin = PurchasingApp.Token(tenant, "admin", "ada");
        try
        {
            var before = DateTimeOffset.UtcNow;
            using var put = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Put, Path, admin, body: new { mode = "Down" }, idempotencyKey: Guid.NewGuid().ToString()));
            put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
            var set = (await put.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
            set.GetProperty("mode").GetString().ShouldBe("Down");
            set.GetProperty("restoresAt").GetDateTimeOffset().ShouldBeInRange(before.AddMinutes(5).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1));

            // The simulator itself is down for the supplier, and the ERP was left as it was.
            var simulator = await SimulatorChaosAsync();
            simulator.GetProperty("supplier").GetProperty("mode").GetString().ShouldBe("Down");
            simulator.GetProperty("erp").GetProperty("mode").GetString().ShouldBe("Normal");
            simulator.GetProperty("supplier").GetProperty("failureRate").GetDouble().ShouldBe(0.1, "only the mode changes");

            using var get = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, Path, admin));
            get.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await get.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mode").GetString().ShouldBe("Down", "the write dropped the cached read");
        }
        finally
        {
            await app.ChaosAsync("Normal");
        }
    }

    [Fact]
    public async Task Supplier_chaos_needs_admin_system()
    {
        var tenant = PurchasingApp.NewTenant();
        var requester = PurchasingApp.Token(tenant);

        using var put = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Put, Path, requester, body: new { mode = "Down" }, idempotencyKey: Guid.NewGuid().ToString()));
        put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var get = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, Path, requester));
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SimulatorChaosAsync()).GetProperty("supplier").GetProperty("mode").GetString().ShouldBe("Normal");

        using var unknown = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Put, Path, PurchasingApp.Token(tenant, "admin", "ada"), body: new { mode = "Exploded" }, idempotencyKey: Guid.NewGuid().ToString()));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JsonElement> SimulatorChaosAsync()
    {
        using var client = app.Simulator.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestAuth.ApiKey);
        return (await client.GetFromJsonAsync<JsonElement>("api/v1/simulators/chaos", Ct)).Clone();
    }
}
