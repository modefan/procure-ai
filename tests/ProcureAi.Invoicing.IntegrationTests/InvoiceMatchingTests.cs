using System.Net;
using System.Net.Http.Json;
using System.Text;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Infrastructure;
using Shouldly;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>Invoices arriving (signed webhook, manual entry), the cumulative three-way match, and Review Focus 2 for the invoice webhook.</summary>
[Collection(InvoicingCollection.Name)]
public sealed class InvoiceMatchingTests(InvoicingApp app)
{
    private static object ManualInvoice(Guid orderId, string number, int quantity, decimal price = 25_200m, decimal? stated = null) => new
    {
        orderId,
        supplierInvoiceNumber = number,
        invoiceDate = "2026-10-27",
        lines = new[] { new { lineNo = 1, quantity, unitPriceExclVat = price } },
        statedTotalInclVat = stated ?? Math.Round(quantity * price * 1.15m, 2),
    };

    private Task<HttpResponseMessage> CreateAsync(string token, object body) =>
        app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Post, "/api/v1/invoicing/invoices", token, body: body));

    /// <summary>
    /// The normal demo, through the real simulator: the supplier invoices the whole order (3) before anything was received, so the
    /// invoice is mismatched ("only 0 received"); the delivery of 3 matches it.
    /// </summary>
    [Fact]
    public async Task Webhook_invoice_for_unreceived_order_is_mismatched_then_matches_after_receipt()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);

        // The order reaches the supplier (as Purchasing's ACL sends it); the simulator invoices it by itself shortly after.
        using (var supplier = app.Simulator.CreateClient())
        {
            supplier.DefaultRequestHeaders.Add("X-Api-Key", SimulatorHost.SimulatorKey);
            using var accepted = await supplier.PostAsJsonAsync("supplier/v1/orders", new
            {
                externalReference = order.OrderId.ToString(),
                buyerReference = $"{tenant}/PO-1001",
                currency = "ZAR",
                lines = new[] { new { lineNumber = 1, articleNumber = "LAP-14", quantity = 3, netPriceCents = 2_520_000L } },
            });
            accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var mismatched = (await app.WaitForAsync(order.RequestId, "InvoiceMismatched")).Single(m => m.Kind == "InvoiceMismatched");
        mismatched.Body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(["Line 1: invoiced 3, but only 0 received and not yet invoiced"]);
        app.Webhooks.Of(order.OrderId).ShouldHaveSingleItem().Status.ShouldBe(200, "the signed delivery was accepted");
        var received = app.Events.Of(order.RequestId).Single(m => m.Kind == "InvoiceReceived");
        received.Text("source").ShouldBe("Webhook");
        received.Text("tenantId").ShouldBe(tenant, "the tenant comes from the order view");

        await app.ReceiveAsync(order, 3);
        await app.WaitForAsync(order.RequestId, "InvoiceMatched");
        app.Events.Of(order.RequestId).Select(m => m.Kind).ShouldBe(["InvoiceReceived", "InvoiceMismatched", "InvoiceMatched"]);

        var invoiceId = Guid.Parse(received.Text("invoiceId")!);
        var (invoice, _) = await app.GetInvoiceAsync(invoiceId, InvoicingApp.Token(tenant));
        var line = invoice.Lines.ShouldHaveSingleItem();
        (line.Ordered, line.Received, line.InvoicedBefore, line.Quantity, line.PriceOk, line.QuantityOk).ShouldBe((3, 3, 0, 3, true, true));
        line.OrderUnitPriceText.ShouldBe("R 25\u00A0200,00");
        app.Metrics().Select(m => m.Name).ShouldContain(InvoicingMetrics.Mismatches);
    }

    /// <summary>
    /// The supplier's invoice comes before Invoicing knows the order, and so does the delivery. The invoice waits without a tenant
    /// (nobody can see it), the delivery is retried (no view yet), and when the order arrives the invoice takes the order's tenant, is
    /// matched, and the retried delivery turns it into a match. Nothing is dead-lettered.
    /// </summary>
    [Fact]
    public async Task Invoice_before_order_view_waits_then_matches()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = InvoicingApp.NewOrder(tenant);
        var deadBefore = await app.DeadLettersAsync();

        var invoiceId = await app.WebhookInvoiceAsync(order.OrderId, "INV-EARLY", 3);
        (await app.ScalarAsync<string>("SELECT tenant_id FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe("");
        (await app.ScalarAsync<string>("SELECT status FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe("AwaitingOrder");
        await app.PublishAsync(
            new GoodsReceived(order.OrderId, order.RequestId, tenant, Guid.NewGuid(), [new ReceivedLine(1, 3)], "gary", DateTimeOffset.UtcNow), tenant);
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).ShouldBeEmpty("nothing is published before the order is known");
        using (var list = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Get, "/api/v1/invoicing/invoices", InvoicingApp.Token(tenant))))
        {
            (await list.Content.ReadFromJsonAsync<PagedResult<InvoiceDto>>(InvoicingApp.Json))!.Items.ShouldBeEmpty("a waiting invoice belongs to no tenant yet");
        }

        await app.PublishAsync(order, tenant);

        await app.WaitForAsync(order.RequestId, "InvoiceMatched");
        app.Events.Of(order.RequestId).Select(m => m.Kind).ShouldBe(["InvoiceReceived", "InvoiceMismatched", "InvoiceMatched"]);
        (await app.ScalarAsync<string>("SELECT tenant_id FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe(tenant);
        (await app.ScalarAsync<long>("SELECT sequence FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe(1);
        (await app.ReceivedAsync(order.OrderId)).ShouldBe(3, "the delivery was retried until the order view existed");
        var (invoice, _) = await app.GetInvoiceAsync(invoiceId, InvoicingApp.Token(tenant));
        invoice.RequestId.ShouldBe(order.RequestId);
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    [Fact]
    public async Task Duplicate_supplier_invoice_number_same_content_200_other_content_409()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var order = await app.CreateOrderAsync(tenant);

        using var first = await CreateAsync(token, ManualInvoice(order.OrderId, "INV-7", 2));
        using var again = await CreateAsync(token, ManualInvoice(order.OrderId, "INV-7", 2));
        using var other = await CreateAsync(token, ManualInvoice(order.OrderId, "INV-7", 3));
        using var webhookOther = await app.PostWebhookAsync(InvoicingApp.InvoiceEvent(order.OrderId, "INV-7", 1));
        using var webhookSame = await app.PostWebhookAsync(InvoicingApp.InvoiceEvent(order.OrderId, "INV-7", 2));

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        again.StatusCode.ShouldBe(HttpStatusCode.OK, "the same invoice again");
        (await again.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Id
            .ShouldBe((await first.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Id);
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict, "another invoice reusing the number");
        webhookOther.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        webhookSame.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE order_id = @o", ("o", order.OrderId))).ShouldBe(1);
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).Count(m => m.Kind == "InvoiceReceived").ShouldBe(1);
    }

    /// <summary>Review Focus 2: a replay of the same signed event, and two deliveries of one event at the same time, record one invoice.</summary>
    [Fact]
    public async Task Replayed_invoice_webhook_records_once()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var eventId = Guid.NewGuid().ToString();
        var body = InvoicingApp.InvoiceEvent(order.OrderId, "INV-R", 3, eventId: eventId);

        var twins = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.PostWebhookAsync(body)));
        using var replay = await app.PostWebhookAsync(body);
        await InvoicingApp.SettleAsync();

        twins.Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, "a duplicate is answered like a success, so the sender stops");
        (await app.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE order_id = @o", ("o", order.OrderId))).ShouldBe(1);
        (await app.ScalarAsync<long>("SELECT count(*) FROM processed_webhooks WHERE source = 'supplier' AND event_id = @e", ("e", eventId))).ShouldBe(1);
        app.Events.Of(order.RequestId).Count(m => m.Kind == "InvoiceReceived").ShouldBe(1);
        app.Events.Of(order.RequestId).Count(m => m.Kind == "InvoiceMismatched").ShouldBe(1);
        foreach (var response in twins)
        {
            response.Dispose();
        }
    }

    /// <summary>Review Focus 2: stale, wrongly signed, tampered and unsigned deliveries are 401; unreadable ones 400; nothing is recorded.</summary>
    [Fact]
    public async Task Stale_forged_or_unreadable_invoice_webhook_changes_nothing()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var body = InvoicingApp.InvoiceEvent(order.OrderId, "INV-F", 3);

        using var expired = await app.PostWebhookAsync(body, DateTimeOffset.UtcNow.AddMinutes(-6));
        using var wrongSecret = await app.PostWebhookAsync(body, secret: "not-the-supplier-secret-0123456789abcdef");
        using var tamperedRequest = new HttpRequestMessage(HttpMethod.Post, InvoicingApp.WebhookPath) { Content = new ByteArrayContent(body) };
        WebhookTestSigner.Sign(tamperedRequest, SimulatorHost.Secret, DateTimeOffset.UtcNow);
        tamperedRequest.Content = new StringContent(Encoding.UTF8.GetString(body).Replace("INV-F", "INV-X"), Encoding.UTF8, "application/json");
        using var tampered = await app.Client.SendAsync(tamperedRequest);
        using var unsigned = await app.Client.PostAsync(
            InvoicingApp.WebhookPath, new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } });
        using var notJson = await app.PostWebhookAsync(Encoding.UTF8.GetBytes("this is not json"));
        using var longEvent = await app.PostWebhookAsync(InvoicingApp.InvoiceEvent(order.OrderId, "INV-F", 3, eventId: new string('e', 201)));
        using var longNumber = await app.PostWebhookAsync(InvoicingApp.InvoiceEvent(order.OrderId, new string('N', 65), 3));
        using var notOurs = await app.PostWebhookAsync(Encoding.UTF8.GetBytes(
            """{"eventId":"e-not-ours","type":"invoice.issued","invoiceNumber":"X-1","supplierCode":"contoso-hardware","externalReference":"someone-else","issueDate":"2026-10-27","lines":[{"lineNumber":1,"articleNumber":"LAP-14","quantity":1,"netPriceCents":100}],"grossTotalCents":115}"""));
        await InvoicingApp.SettleAsync();

        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "older than 5 minutes");
        wrongSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "bad signature");
        tampered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "body changed after signing");
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no signature header");
        notJson.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        longEvent.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an event ID the inbox cannot store");
        longNumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an invoice number the column cannot store");
        notOurs.StatusCode.ShouldBe(HttpStatusCode.OK, "not one of our orders: nothing to record, and nothing to resend");
        (await app.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE order_id = @o", ("o", order.OrderId))).ShouldBe(0);
        app.Events.Of(order.RequestId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Manual_invoice_for_an_order_out_of_scope_or_unknown_404()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant, costCenter: "CC-900");

        // The stub gives every user CC-100: rita did not raise this order and CC-900 is not hers.
        using var outOfScope = await CreateAsync(InvoicingApp.Token(tenant, "requester", "rachel"), ManualInvoice(order.OrderId, "INV-S", 1));
        using var unknown = await CreateAsync(InvoicingApp.Token(tenant), ManualInvoice(Guid.NewGuid(), "INV-U", 1));
        using var otherTenant = await CreateAsync(InvoicingApp.Token(InvoicingApp.NewTenant()), ManualInvoice(order.OrderId, "INV-T", 1));

        outOfScope.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, "finance enters invoices only for orders Invoicing knows");
        otherTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE order_id = @o", ("o", order.OrderId))).ShouldBe(0);
    }

    [Fact]
    public async Task Other_tenant_invoice_404()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        await app.ReceiveAsync(order, 3);
        var invoiceId = await app.WebhookInvoiceAsync(order.OrderId, "INV-OT", 3);
        await app.WaitForStatusAsync(invoiceId, "Matched");
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, InvoicingApp.Token(tenant));
        var stranger = InvoicingApp.Token(InvoicingApp.NewTenant());

        using var get = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Get, $"/api/v1/invoicing/invoices/{invoiceId}", stranger));
        using var pay = await app.PayAsync(invoiceId, stranger, etag);
        using var hold = await app.SendHttpAsync(InvoicingApp.Request(
            HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoiceId}/hold", stranger, etag, new { reason = "Looks wrong" }));
        using var list = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Get, $"/api/v1/invoicing/invoices?orderId={order.OrderId}", stranger));

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        pay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        hold.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await list.Content.ReadFromJsonAsync<PagedResult<InvoiceDto>>(InvoicingApp.Json))!.Items.ShouldBeEmpty();
        (await app.ScalarAsync<long>("SELECT count(*) FROM payments p JOIN invoices i ON i.id = p.invoice_id WHERE i.id = @id", ("id", invoiceId)))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Invoice_document_is_stored_as_jsonb()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var invoiceId = await app.WebhookInvoiceAsync(order.OrderId, "INV-J", 3);
        await app.WaitForStatusAsync(invoiceId, "Mismatched");

        (await app.ScalarAsync<string>("SELECT pg_typeof(document)::text FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe("jsonb");
        (await app.ScalarAsync<string>("SELECT document->>'invoiceNumber' FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe("INV-J");
        (await app.ScalarAsync<long>("SELECT (document->>'grossTotalCents')::bigint FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe(8_694_000);
        (await app.ScalarAsync<string>("SELECT match->'reasons'->>0 FROM invoices WHERE id = @id", ("id", invoiceId)))
            .ShouldBe("Line 1: invoiced 3, but only 0 received and not yet invoiced");
    }
}
