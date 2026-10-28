using System.Net;
using System.Net.Http.Json;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using Shouldly;
using static ProcureAi.Simulators.Tests.SimulatorFactory;

namespace ProcureAi.Simulators.Tests;

public sealed class SimulatorTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static Dictionary<string, string?> NoAutoInvoice() => new() { ["Simulators:Supplier:AutoInvoice"] = "false" };

    private static object Chaos(string supplierMode, double supplierRate, string erpMode, double erpRate) => new
    {
        supplier = new { mode = supplierMode, failureRate = supplierRate, minLatency = "00:00:00", maxLatency = "00:00:00" },
        erp = new { mode = erpMode, failureRate = erpRate, minLatency = "00:00:00", maxLatency = "00:00:00" },
    };

    private static async Task<string> PlaceOrderAsync(HttpClient client, string externalReference, params (int, string, int, long)[] lines)
    {
        var response = await client.PostAsJsonAsync("supplier/v1/orders", OrderBody(externalReference, lines.Length == 0 ? [(1, "PEN-01", 2, 1000)] : lines));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await ReadAsync(response);
        body.GetProperty("status").GetString().ShouldBe("RECEIVED");
        return body.GetProperty("supplierOrderId").GetString()!;
    }

    [Fact]
    public async Task Order_is_acknowledged_by_signed_webhook_after_delay()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        using var client = factory.SupplierClient();

        var orderId = await PlaceOrderAsync(client, "order-1");
        orderId.ShouldStartWith("SO-");

        factory.Time.Advance(TimeSpan.FromSeconds(2));
        await SettleAsync();
        factory.Receiver.Received.ShouldBeEmpty("the acknowledgement comes after Supplier:ConfirmDelay (3 s)");

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count > 0, TimeSpan.FromSeconds(1));
        var webhook = factory.Receiver.Received.Single();
        webhook.Url.ToString().ShouldBe(PurchasingUrl);
        webhook.Type.ShouldBe("order.acknowledged");
        webhook.Json.GetProperty("supplierOrderId").GetString().ShouldBe(orderId);
        webhook.Json.GetProperty("externalReference").GetString().ShouldBe("order-1");
        webhook.Json.GetProperty("expectedDeliveryDate").GetString().ShouldBe("2026-11-01");
        webhook.Json.GetProperty("eventId").GetString().ShouldNotBeNullOrWhiteSpace();
        WebhookSignature.Verify(Secret, webhook.Signature, webhook.Body, factory.Time.GetUtcNow(), Tolerance)
            .ShouldBe(WebhookVerification.Valid);
    }

    [Fact]
    public async Task Same_external_reference_twice_one_order()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        using var client = factory.SupplierClient();

        var first = await PlaceOrderAsync(client, "order-dup");
        var second = await PlaceOrderAsync(client, "order-dup");
        var other = await PlaceOrderAsync(client, "order-other");

        second.ShouldBe(first);
        other.ShouldNotBe(first);

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count >= 2, TimeSpan.FromSeconds(1));
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        factory.Receiver.Received.Count(w => w.Json.GetProperty("externalReference").GetString() == "order-dup").ShouldBe(1);
    }

    /// <summary>
    /// Test hook: what the supplier holds for one buyer reference (our tenant/order number) and how often each was sent, so a system
    /// test can prove an order was sent once and became one supplier order.
    /// </summary>
    [Fact]
    public async Task Admin_lists_the_orders_of_a_buyer_reference_with_how_often_each_was_received()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        using var client = factory.SupplierClient();
        using var admin = factory.AdminClient();

        var first = await PlaceOrderAsync(client, "order-q");
        await PlaceOrderAsync(client, "order-q");
        var other = await PlaceOrderAsync(client, "order-other");

        var listed = await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001"));
        var orders = listed.EnumerateArray().ToDictionary(o => o.GetProperty("externalReference").GetString()!);
        orders.Count.ShouldBe(2, "two external references, so two supplier orders");
        orders["order-q"].GetProperty("supplierOrderId").GetString().ShouldBe(first);
        orders["order-q"].GetProperty("timesReceived").GetInt32().ShouldBe(2);
        orders["order-other"].GetProperty("supplierOrderId").GetString().ShouldBe(other);
        orders["order-other"].GetProperty("timesReceived").GetInt32().ShouldBe(1);
        orders["order-q"].GetProperty("cancelled").GetBoolean().ShouldBeFalse();

        var none = await ReadAsync(await admin.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-9999"));
        none.GetArrayLength().ShouldBe(0);
        (await client.GetAsync("api/v1/simulators/supplier/orders?buyerReference=demo%2FPO-0001")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Discontinued_article_is_declined()
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.SupplierClient();

        await PlaceOrderAsync(client, "order-disc", (1, "PEN-01", 1, 500), (2, "DISC-01", 1, 900));

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count > 0, TimeSpan.FromSeconds(1));
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();

        var webhook = factory.Receiver.Received.Single();
        webhook.Type.ShouldBe("order.declined");
        webhook.Json.GetProperty("declineReason").GetString().ShouldBe("Article DISC-01 is discontinued");
        factory.Receiver.Received.ShouldNotContain(w => w.Type == "invoice.issued", "a declined order is never invoiced");
    }

    [Fact]
    public async Task Auto_invoice_bills_ordered_quantities()
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.SupplierClient();

        await PlaceOrderAsync(client, "order-inv", (1, "PEN-01", 3, 1000), (2, "PAD-02", 2, 2550));

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Any(w => w.Type == "invoice.issued"), TimeSpan.FromSeconds(1));
        var invoice = factory.Receiver.Received.Single(w => w.Type == "invoice.issued");
        invoice.Url.ToString().ShouldBe(InvoicingUrl);
        var json = invoice.Json;
        json.GetProperty("invoiceNumber").GetString().ShouldStartWith("INV-");
        json.GetProperty("externalReference").GetString().ShouldBe("order-inv");
        json.GetProperty("supplierCode").GetString().ShouldNotBeNullOrWhiteSpace();
        json.GetProperty("issueDate").GetString().ShouldNotBeNullOrWhiteSpace();
        var lines = json.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(2);
        lines[0].GetProperty("quantity").GetInt32().ShouldBe(3);
        lines[0].GetProperty("netPriceCents").GetInt64().ShouldBe(1000);
        lines[1].GetProperty("articleNumber").GetString().ShouldBe("PAD-02");
        lines[1].GetProperty("quantity").GetInt32().ShouldBe(2);
        lines[1].GetProperty("netPriceCents").GetInt64().ShouldBe(2550);
        json.GetProperty("netTotalCents").GetInt64().ShouldBe(8100);
        json.GetProperty("vatCents").GetInt64().ShouldBe(1215);
        json.GetProperty("grossTotalCents").GetInt64().ShouldBe(9315);
        WebhookSignature.Verify(Secret, invoice.Signature, invoice.Body, factory.Time.GetUtcNow(), Tolerance)
            .ShouldBe(WebhookVerification.Valid);
    }

    /// <summary>
    /// The invoice quotes the supplier code the buyer gave on the order (Purchasing names the order's catalog supplier), so the
    /// buyer's three-way match sees the invoice is from the order's supplier; an order without one gets the configured code.
    /// </summary>
    [Fact]
    public async Task Invoice_quotes_the_supplier_code_of_its_order()
    {
        await using var factory = new SimulatorFactory(new() { ["Simulators:Supplier:SupplierCode"] = "SIMSUP" });
        using var client = factory.SupplierClient();
        var named = new
        {
            externalReference = "order-named",
            buyerReference = "demo/PO-0002",
            supplierCode = "fabrikam-office",
            currency = "ZAR",
            lines = new[] { new { lineNumber = 1, articleNumber = "PEN-01", quantity = 1, netPriceCents = 100L } },
        };
        (await factory.WithClockAsync(client.PostAsJsonAsync("supplier/v1/orders", named))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await factory.WithClockAsync(client.PostAsJsonAsync("supplier/v1/orders", OrderBody("order-unnamed", (1, "PEN-01", 1, 100)))))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count(w => w.Type == "invoice.issued") == 2, TimeSpan.FromSeconds(1));
        var invoices = factory.Receiver.Received.Where(w => w.Type == "invoice.issued")
            .ToDictionary(w => w.Json.GetProperty("externalReference").GetString()!, w => w.Json.GetProperty("supplierCode").GetString());
        invoices["order-named"].ShouldBe("fabrikam-office");
        invoices["order-unnamed"].ShouldBe("SIMSUP", "no code given: the configured one");
    }

    [Fact]
    public async Task Cancel_drops_pending_webhooks()
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.SupplierClient();

        var orderId = await PlaceOrderAsync(client, "order-cancel");
        var cancel = await client.PostAsync($"supplier/v1/orders/{orderId}/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(cancel)).GetProperty("status").GetString().ShouldBe("CANCELLED");

        // Idempotent, and unknown orders are 404.
        (await client.PostAsync($"supplier/v1/orders/{orderId}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync("supplier/v1/orders/SO-nope/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        factory.Time.Advance(TimeSpan.FromMinutes(2));
        await SettleAsync();
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        await SettleAsync();
        factory.Receiver.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_mode_returns_503_for_supplier_and_erp()
    {
        await using var factory = new SimulatorFactory();
        using var admin = factory.AdminClient();
        using var client = factory.SupplierClient();

        (await admin.PutAsJsonAsync("api/v1/simulators/chaos", Chaos("Down", 0.1, "Down", 0.1))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var order = await factory.WithClockAsync(client.PostAsJsonAsync("supplier/v1/orders", OrderBody("order-down", (1, "PEN-01", 1, 100))));
        order.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var payment = await factory.WithClockAsync(client.GetAsync("erp/v1/payments/PAY-1"));
        payment.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();
        factory.Receiver.Received.ShouldBeEmpty("a refused order creates nothing");
    }

    [Fact]
    public async Task Failing_mode_fails_about_the_failure_rate()
    {
        await using var factory = new SimulatorFactory();
        using var admin = factory.AdminClient();
        using var client = factory.SupplierClient();
        (await admin.PutAsJsonAsync("api/v1/simulators/chaos", Chaos("Normal", 0.1, "Failing", 0.3))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var failures = 0;
        for (var i = 0; i < 1000; i++)
        {
            var response = await client.GetAsync($"erp/v1/payments/PAY-{i}");
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                failures++;
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a call that is not failed reaches the endpoint");
            }
        }

        failures.ShouldBeInRange(250, 350);
    }

    [Fact]
    public async Task Slow_mode_multiplies_the_latency_by_five()
    {
        await using var factory = new SimulatorFactory(new()
        {
            ["Simulators:Chaos:Erp:Mode"] = "Slow",
            ["Simulators:Chaos:Erp:MinLatency"] = "00:00:01",
            ["Simulators:Chaos:Erp:MaxLatency"] = "00:00:01",
        });
        using var client = factory.SupplierClient();

        var request = client.GetAsync("erp/v1/payments/PAY-1");
        await Task.Delay(100);
        factory.Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        request.IsCompleted.ShouldBeFalse("1 s x 5 = 5 s");

        factory.Time.Advance(TimeSpan.FromSeconds(1.1));
        (await request).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Erp_payment_is_idempotent_by_reference()
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.SupplierClient();
        var lines = new[] { new { lineNumber = 1, articleNumber = "PEN-01", quantity = 3, netAmountCents = 3000 } };
        var payment = new { paymentReference = "PAY-100", vendorCode = "SIMSUP", invoiceNumber = "INV-1", currency = "ZAR", grossAmountCents = 9315, lines };

        var first = await client.PostAsJsonAsync("erp/v1/payments", payment);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var number = (await ReadAsync(first)).GetProperty("documentNumber").GetString()!;
        number.ShouldStartWith("ERP-");

        var second = await client.PostAsJsonAsync("erp/v1/payments", payment);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(second)).GetProperty("documentNumber").GetString().ShouldBe(number);

        var other = await client.PostAsJsonAsync("erp/v1/payments", payment with { paymentReference = "PAY-101" });
        (await ReadAsync(other)).GetProperty("documentNumber").GetString().ShouldNotBe(number);

        var get = await client.GetAsync("erp/v1/payments/PAY-100");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadAsync(get);
        body.GetProperty("documentNumber").GetString().ShouldBe(number);
        body.GetProperty("grossAmountCents").GetInt64().ShouldBe(9315);
        (await client.GetAsync("erp/v1/payments/PAY-none")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    [InlineData(AdminKey)]
    public async Task Missing_api_key_401(string? key)
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.ClientWithKey(key);

        (await client.PostAsJsonAsync("supplier/v1/orders", OrderBody("x", (1, "A", 1, 1)))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("supplier/v1/orders/SO-1/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("erp/v1/payments/PAY-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("erp/v1/payments", new { paymentReference = "p" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Chaos_needs_admin_key()
    {
        await using var factory = new SimulatorFactory();
        var chaos = Chaos("Down", 0.5, "Normal", 0.5);

        foreach (var key in new string?[] { null, "wrong", SimulatorKey })
        {
            using var client = factory.ClientWithKey(key);
            (await client.GetAsync("api/v1/simulators/chaos")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await client.PutAsJsonAsync("api/v1/simulators/chaos", chaos)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var admin = factory.AdminClient();
        var initial = await ReadAsync(await admin.GetAsync("api/v1/simulators/chaos"));
        initial.GetProperty("supplier").GetProperty("mode").GetString().ShouldBe("Normal");
        initial.GetProperty("supplier").GetProperty("failureRate").GetDouble().ShouldBe(0.1);

        (await admin.PutAsJsonAsync("api/v1/simulators/chaos", chaos)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await ReadAsync(await admin.GetAsync("api/v1/simulators/chaos"));
        updated.GetProperty("supplier").GetProperty("mode").GetString().ShouldBe("Down");
        updated.GetProperty("supplier").GetProperty("failureRate").GetDouble().ShouldBe(0.5);

        (await admin.PutAsJsonAsync("api/v1/simulators/chaos", Chaos("Normal", 2.0, "Normal", 0.1))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signature_verifies_with_the_building_block()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        using var client = factory.SupplierClient();
        await PlaceOrderAsync(client, "order-sig");
        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count > 0, TimeSpan.FromSeconds(1));
        var webhook = factory.Receiver.Received.Single();
        var now = factory.Time.GetUtcNow();

        WebhookSignature.Verify(Secret, webhook.Signature, webhook.Body, now, Tolerance).ShouldBe(WebhookVerification.Valid);
        WebhookSignature.Verify("another-secret", webhook.Signature, webhook.Body, now, Tolerance).ShouldBe(WebhookVerification.BadSignature);
        WebhookSignature.Verify(Secret, webhook.Signature, [.. webhook.Body, (byte)' '], now, Tolerance).ShouldBe(WebhookVerification.BadSignature);
        WebhookSignature.Verify(Secret, webhook.Signature, webhook.Body, now.AddHours(1), Tolerance).ShouldBe(WebhookVerification.Expired);
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_the_same_event()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        factory.Receiver.Respond = (_, count) => count < 2 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
        using var client = factory.SupplierClient();
        await PlaceOrderAsync(client, "order-retry");

        // The first attempt fails, the second (1 s later) succeeds; it is the same event.
        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count >= 2, TimeSpan.FromMilliseconds(500));
        var attempts = factory.Receiver.Received;
        attempts[1].Json.GetProperty("eventId").GetString().ShouldBe(attempts[0].Json.GetProperty("eventId").GetString());
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        await SettleAsync();
        factory.Receiver.Received.Count.ShouldBe(2, "delivered, so no more attempts");
    }

    [Fact]
    public async Task Delivery_gives_up_after_the_last_retry()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        factory.Receiver.Respond = (_, _) => HttpStatusCode.ServiceUnavailable;
        using var client = factory.SupplierClient();
        await PlaceOrderAsync(client, "order-drop");

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Count >= 4, TimeSpan.FromMilliseconds(500));
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await SettleAsync();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await SettleAsync();
        factory.Receiver.Received.Count.ShouldBe(4, "the first attempt and three retries (1 s, 5 s, 30 s), then the webhook is dropped");
    }

    [Fact]
    public async Task Admin_can_issue_an_invoice_with_a_price_deviation()
    {
        await using var factory = new SimulatorFactory(NoAutoInvoice());
        using var client = factory.SupplierClient();
        using var admin = factory.AdminClient();
        var orderId = await PlaceOrderAsync(client, "order-manual", (1, "PEN-01", 4, 1000), (2, "PAD-02", 2, 500));

        (await client.PostAsJsonAsync($"api/v1/simulators/supplier/orders/{orderId}/invoice", new { })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await admin.PostAsJsonAsync("api/v1/simulators/supplier/orders/SO-nope/invoice", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var issue = await admin.PostAsJsonAsync($"api/v1/simulators/supplier/orders/{orderId}/invoice",
            new { lines = new[] { new { lineNumber = 1, quantity = 3 } }, priceDeviationCents = 50, invoiceNumber = "INV-MANUAL-1" });
        issue.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await factory.AdvanceUntilAsync(() => factory.Receiver.Received.Any(w => w.Type == "invoice.issued"), TimeSpan.FromMilliseconds(500));
        var json = factory.Receiver.Received.Single(w => w.Type == "invoice.issued").Json;
        json.GetProperty("invoiceNumber").GetString().ShouldBe("INV-MANUAL-1");
        var lines = json.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(1);
        lines[0].GetProperty("quantity").GetInt32().ShouldBe(3);
        lines[0].GetProperty("netPriceCents").GetInt64().ShouldBe(1050);
        json.GetProperty("netTotalCents").GetInt64().ShouldBe(3150);

        (await admin.PostAsJsonAsync($"api/v1/simulators/supplier/orders/{orderId}/invoice",
            new { lines = new[] { new { lineNumber = 9, quantity = 1 } } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_order_is_400()
    {
        await using var factory = new SimulatorFactory();
        using var client = factory.SupplierClient();

        var wrongCurrency = new { externalReference = "x", buyerReference = "b", currency = "USD", lines = new[] { new { lineNumber = 1, articleNumber = "A", quantity = 1, netPriceCents = 1 } } };
        (await client.PostAsJsonAsync("supplier/v1/orders", wrongCurrency)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var noLines = new { externalReference = "x", buyerReference = "b", currency = "ZAR", lines = Array.Empty<object>() };
        (await client.PostAsJsonAsync("supplier/v1/orders", noLines)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_receiver_url_drops_the_webhook_and_keeps_running()
    {
        await using var factory = new SimulatorFactory(new() { ["Simulators:Webhooks:PurchasingUrl"] = "", ["Simulators:Supplier:AutoInvoice"] = "false" });
        using var client = factory.SupplierClient();
        await PlaceOrderAsync(client, "order-nourl");

        factory.Time.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        factory.Receiver.Received.ShouldBeEmpty();
        (await client.GetAsync("erp/v1/payments/x")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
