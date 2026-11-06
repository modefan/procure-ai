using System.Net;
using System.Net.Http.Json;
using Shouldly;
using static ProcureAi.Simulators.Tests.SimulatorFactory;

namespace ProcureAi.Simulators.Tests;

/// <summary>
/// The simulators' part of the nightly demo reset (plan 06 ruling 4): orders and postings forgotten, numbers from the start again, waiting
/// webhooks dropped (an old one must never reach the new order that got the same number), chaos back to Normal. Admin key only.
/// </summary>
public sealed class SimulatorResetTests
{
    private static object Down => new
    {
        supplier = new { mode = "Down", failureRate = 0.1, minLatency = "00:00:00", maxLatency = "00:00:00" },
        erp = new { mode = "Slow", failureRate = 0.1, minLatency = "00:00:00", maxLatency = "00:00:00" },
    };

    [Fact]
    public async Task Reset_forgets_orders_restarts_numbers_drops_waiting_webhooks_and_restores_chaos()
    {
        await using var factory = new SimulatorFactory(Enabled);
        using var supplier = factory.SupplierClient();
        using var admin = factory.AdminClient();

        var first = await PlaceAsync(supplier, "acme/PO-1001");
        first.ShouldBe("SO-1001");
        var payment = new { paymentReference = "PAY-1", vendorCode = "SIMSUP", invoiceNumber = "INV-1", currency = "ZAR", grossAmountCents = 1150 };
        (await ReadAsync(await supplier.PostAsJsonAsync("erp/v1/payments", payment))).GetProperty("documentNumber").GetString().ShouldBe("ERP-5000");
        (await admin.PutAsJsonAsync("api/v1/simulators/chaos", Down)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var resetAt = factory.Time.GetUtcNow();
        (await admin.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var chaos = await ReadAsync(await admin.GetAsync("api/v1/simulators/chaos"));
        chaos.GetProperty("supplier").GetProperty("mode").GetString().ShouldBe("Normal");
        chaos.GetProperty("erp").GetProperty("mode").GetString().ShouldBe("Normal");
        (await supplier.GetAsync("erp/v1/payments/PAY-1")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001"))).GetArrayLength().ShouldBe(0);

        // The same number again for a new order; only its own webhooks arrive (acknowledgement and invoice), none of the old order's.
        var second = await PlaceAsync(supplier, "acme/PO-1001-again");
        second.ShouldBe("SO-1001");
        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count >= 2, TimeSpan.FromSeconds(1));
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await SettleAsync();
        factory.Receiver.Received.Select(w => w.Json.TryGetProperty("externalReference", out var r) ? r.GetString() : null)
            .ShouldAllBe(r => r == "acme/PO-1001-again");
        factory.Receiver.Received.Single(w => w.Type == "invoice.issued").Json.GetProperty("invoiceNumber").GetString()
            .ShouldBe($"INV-{resetAt:yyMMddHHmmss}-1001", "a new series that starts at the reset");
        (await ReadAsync(await supplier.PostAsJsonAsync("erp/v1/payments", payment))).GetProperty("documentNumber").GetString().ShouldBe("ERP-5000");

        // Twice is safe.
        (await admin.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SimulatorKey)]
    public async Task Reset_needs_the_admin_key(string? key)
    {
        await using var factory = new SimulatorFactory(Enabled);
        using var client = factory.ClientWithKey(key);
        using var supplier = factory.SupplierClient();
        await PlaceAsync(supplier, "acme/PO-1001");

        (await client.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var admin = factory.AdminClient();
        (await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001"))).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Reset_disabled_is_404()
    {
        // DemoReset:Enabled off (the default, every deployment but the demo's): the route does not exist, like the services' reset. Nothing
        // is forgotten, so live orders and the SO-/INV-/ERP- numbering stay as they are.
        await using var factory = new SimulatorFactory();
        using var admin = factory.AdminClient();
        using var supplier = factory.SupplierClient();
        (await PlaceAsync(supplier, "acme/PO-1001")).ShouldBe("SO-1001");

        (await admin.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var anonymous = factory.ClientWithKey(null);
        (await anonymous.PostAsync("api/v1/simulators/reset", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // ProcureAi.DemoData's preflight probe (a GET) sees the same 404 before it touches anything.
        (await admin.GetAsync("api/v1/simulators/reset")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001"))).GetArrayLength().ShouldBe(1);
        (await PlaceAsync(supplier, "acme/PO-1002")).ShouldBe("SO-1002");
    }

    [Fact]
    public async Task Reset_enabled_answers_the_preflight_probe_with_405()
    {
        // The probe ProcureAi.DemoData sends before any reset: a GET on the POST-only route. 405 means "there, switched on"; it runs nothing.
        await using var factory = new SimulatorFactory(Enabled);
        using var admin = factory.AdminClient();
        using var supplier = factory.SupplierClient();
        await PlaceAsync(supplier, "acme/PO-1001");

        (await admin.GetAsync("api/v1/simulators/reset")).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        (await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001"))).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Simulator_numbers_do_not_repeat_after_restart()
    {
        // Invoicing refuses an invoice number it already holds, and the simulator keeps its numbers in memory only: a restart (pod
        // rescheduled, image update) must not hand out a number of an invoice issued before it (plan 07 carry-over 26).
        var noAutoInvoice = new Dictionary<string, string?> { ["Simulators:Supplier:AutoInvoice"] = "false" };
        var before = new List<string>();
        await using (var first = new SimulatorFactory(noAutoInvoice))
        {
            using var supplier = first.SupplierClient();
            using var admin = first.AdminClient();
            before.Add(await InvoiceAsync(admin, await PlaceAsync(supplier, "acme/PO-1001")));
            before.Add(await InvoiceAsync(admin, await PlaceAsync(supplier, "acme/PO-1002")));
        }

        before.Distinct().Count().ShouldBe(2);

        // Started again a minute later, with empty memory.
        await using var again = new SimulatorFactory(noAutoInvoice, DefaultStart.AddMinutes(1));
        using var supplierAgain = again.SupplierClient();
        using var adminAgain = again.AdminClient();
        var after = await InvoiceAsync(adminAgain, await PlaceAsync(supplierAgain, "acme/PO-1003"));

        before.ShouldNotContain(after);
        after.ShouldStartWith("INV-");
    }

    private static async Task<string> InvoiceAsync(HttpClient admin, string supplierOrderId)
    {
        var response = await admin.PostAsJsonAsync($"api/v1/simulators/supplier/orders/{supplierOrderId}/invoice", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await ReadAsync(response)).GetProperty("invoiceNumber").GetString()!;
    }

    private static Dictionary<string, string?> Enabled => new() { ["DemoReset:Enabled"] = "true" };

    private static async Task<string> PlaceAsync(HttpClient supplier, string externalReference)
    {
        var response = await supplier.PostAsJsonAsync("supplier/v1/orders", OrderBody(externalReference, (1, "PEN-01", 2, 1000)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await ReadAsync(response)).GetProperty("supplierOrderId").GetString()!;
    }
}
