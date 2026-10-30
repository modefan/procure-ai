using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Domain;
using ProcureAi.Invoicing.Infrastructure;
using Shouldly;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>Payments over HTTP: amounts, separation of duties, hold, If-Match races (Review Focus 3), ERP export, settlement, CDC.</summary>
[Collection(InvoicingCollection.Name)]
public sealed class PaymentTests(InvoicingApp app, ITestOutputHelper output)
{
    private static readonly HttpStatusCode[] Lost = [HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict];

    /// <summary>An order of 3 laptops at R 25 200,00 with <paramref name="received"/> delivered and one webhook invoice of <paramref name="invoiced"/>.</summary>
    private async Task<(PurchaseOrderCreated Order, Guid InvoiceId)> InvoicedOrderAsync(string tenant, int received, int invoiced, string number = "INV-1")
    {
        var order = await app.CreateOrderAsync(tenant);
        if (received > 0)
        {
            await app.ReceiveAsync(order, received);
        }

        var invoiceId = await app.WebhookInvoiceAsync(order.OrderId, number, invoiced);
        await app.WaitForStatusAsync(invoiceId, invoiced <= received ? "Matched" : "Mismatched");
        return (order, invoiceId);
    }

    private async Task<long> PaidOnOrderAsync(Guid orderId) =>
        await app.ScalarAsync<int>("SELECT paid FROM order_view_lines WHERE order_id = @id AND line_no = 1", ("id", orderId));

    private async Task<long> PaymentsOfAsync(Guid invoiceId) =>
        await app.ScalarAsync<long>("SELECT count(*) FROM payments WHERE invoice_id = @id", ("id", invoiceId));

    /// <summary>Wireframe 11: invoice of 3, 2 received: finance pays the 2 that are payable, R 57 960,00 with VAT, exported to the ERP.</summary>
    [Fact]
    public async Task Partial_payment_R57960_publishes_PaymentReleased_and_exports_to_erp()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 2, invoiced: 3);
        var (before, etag) = await app.GetInvoiceAsync(invoiceId, token);
        before.MismatchReasons.ShouldBe(["Line 1: invoiced 3, but only 2 received and not yet invoiced"]);

        using var response = await app.PayAsync(invoiceId, token, etag);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var paid = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!;
        paid.Status.ShouldBe(InvoiceStatus.PartiallyPaid);
        var payment = paid.Payments.ShouldHaveSingleItem();
        (payment.TotalExclVat, payment.Vat, payment.TotalInclVat).ShouldBe((50_400m, 7_560m, 57_960m));
        payment.TotalInclVatText.ShouldBe("R 57\u00A0960,00");
        payment.Number.ShouldBe("PAY-0001");
        payment.ReleasedBy.ShouldBe(InvoicingApp.Finance);

        var released = (await app.WaitForAsync(order.RequestId, "PaymentReleased")).Single(m => m.Kind == "PaymentReleased");
        released.Body.GetProperty("amountInclVat").GetDecimal().ShouldBe(57_960m);
        var line = released.Body.GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
        (line.GetProperty("lineNo").GetInt32(), line.GetProperty("quantity").GetInt32(), line.GetProperty("amountExclVat").GetDecimal()).ShouldBe((1, 2, 50_400m));

        var exported = (await app.WaitForAsync(order.RequestId, "PaymentExported")).Single(m => m.Kind == "PaymentExported");
        var document = app.Simulator.Erp.Find(payment.Id.ToString()).ShouldNotBeNull("the ERP knows the payment by our payment ID");
        exported.Text("erpReference").ShouldBe(document.DocumentNumber);
        (document.VendorCode, document.InvoiceNumber, document.Currency, document.GrossAmountCents).ShouldBe((InvoicingApp.Supplier, "INV-1", "ZAR", 5_796_000L));
        var erpLine = document.Lines.ShouldHaveSingleItem();
        (erpLine.LineNumber, erpLine.ArticleNumber, erpLine.Quantity, erpLine.NetAmountCents).ShouldBe((1, "LAP-14", 2, 5_040_000L));
        await InvoicingApp.EventuallyAsync(
            async () => await app.ScalarAsync<string>("SELECT export_status FROM payments WHERE id = @id", ("id", payment.Id)) == "Exported", "the export");
        var (after, _) = await app.GetInvoiceAsync(invoiceId, token);
        after.Payments.Single().ErpReference.ShouldBe(document.DocumentNumber);
    }

    [Fact]
    public async Task Payment_has_released_by_name_for_other_releaser()
    {
        var tenant = InvoicingApp.NewTenant();
        app.Access.SetDisplayName(tenant, InvoicingApp.Finance, "Fiona Finance");
        var (_, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, InvoicingApp.Token(tenant));
        using var pay = await app.PayAsync(invoiceId, InvoicingApp.Token(tenant), etag);
        pay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await pay.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Payments.Single().ReleasedByName.ShouldBe("Fiona Finance");

        // Another user (another finance user) reads it: the releaser is Fiona, not the caller.
        var (invoice, _) = await app.GetInvoiceAsync(invoiceId, InvoicingApp.Token(tenant, "finance", "fred"));

        invoice.Payments.Single().ReleasedBy.ShouldBe(InvoicingApp.Finance);
        invoice.Payments.Single().ReleasedByName.ShouldBe("Fiona Finance");
    }

    /// <summary>Review Focus 3: ten payment POSTs on one invoice at once (different idempotency keys, the same If-Match): one payment.</summary>
    [Fact]
    public async Task Ten_parallel_payments_one_payment()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => app.PayAsync(invoiceId, token, etag)));
        var statuses = responses.Select(r => r.StatusCode).ToList();
        output.WriteLine(string.Join(", ", statuses));

        statuses.Count(s => s == HttpStatusCode.OK).ShouldBe(1);
        statuses.Where(s => s != HttpStatusCode.OK).ShouldAllBe(s => Lost.Contains(s));
        (await PaymentsOfAsync(invoiceId)).ShouldBe(1);
        (await PaidOnOrderAsync(order.OrderId)).ShouldBe(3);
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).Count(m => m.Kind == "PaymentReleased").ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Task 0c: a losing payment that read the invoice before the winner committed, and its order view after (two statements, two
    /// snapshots under READ COMMITTED), still holds the old version, so the If-Match check passes and the rules see the winner's
    /// paid quantities: "Nothing can be paid" (422). The race must reach the caller as a conflict (412/409), never as a business rule.
    /// The pause forces that interleaving: the loser stops before its second read while the winner pays.
    /// </summary>
    [Fact]
    public async Task Losing_concurrent_release_gets_conflict_not_422()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
        app.ReadPause.Arm();
        var paused = InvoicingApp.Request(HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoiceId}/payments", token, etag);
        paused.Headers.TryAddWithoutValidation(InvoiceReadPause.Header, "1");

        HttpResponseMessage lost;
        var losing = app.SendHttpAsync(paused);
        try
        {
            await app.ReadPause.Reached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            using var winning = await app.PayAsync(invoiceId, token, etag);
            winning.StatusCode.ShouldBe(HttpStatusCode.OK, await winning.Content.ReadAsStringAsync());
        }
        finally
        {
            app.ReadPause.Release();
            lost = await losing;
        }

        using (lost)
        {
            Lost.ShouldContain(lost.StatusCode, await lost.Content.ReadAsStringAsync());
        }

        (await PaymentsOfAsync(invoiceId)).ShouldBe(1);
        (await PaidOnOrderAsync(order.OrderId)).ShouldBe(3);
        (await app.ScalarAsync<int>("SELECT paid FROM invoice_lines WHERE invoice_id = @id AND line_no = 1", ("id", invoiceId))).ShouldBe(3);
        (await app.ScalarAsync<string>("SELECT status FROM invoices WHERE id = @id", ("id", invoiceId))).ShouldBe(nameof(InvoiceStatus.Paid));
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).Count(m => m.Kind == "PaymentReleased").ShouldBe(1);
    }

    /// <summary>
    /// Review Focus 3: two invoices of 2 on a line with 3 received, both paid at the same moment, five times: never more than the 3
    /// received is paid. Whichever pays first takes 2, the other the 1 left (the order view's version serialises them).
    /// </summary>
    [Fact]
    public async Task Parallel_payments_on_two_invoices_never_exceed_received()
    {
        for (var round = 0; round < 5; round++)
        {
            var tenant = InvoicingApp.NewTenant();
            var token = InvoicingApp.Token(tenant);
            var order = await app.CreateOrderAsync(tenant);
            await app.ReceiveAsync(order, 3);
            var first = await app.WebhookInvoiceAsync(order.OrderId, "INV-A", 2);
            await app.WaitForStatusAsync(first, "Matched");
            var second = await app.WebhookInvoiceAsync(order.OrderId, "INV-B", 2);
            await app.WaitForStatusAsync(second, "Mismatched");
            var (_, firstTag) = await app.GetInvoiceAsync(first, token);
            var (_, secondTag) = await app.GetInvoiceAsync(second, token);

            var responses = await Task.WhenAll(app.PayAsync(first, token, firstTag), app.PayAsync(second, token, secondTag));
            var detail = $"round {round}: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}";
            output.WriteLine(detail);

            responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK, detail);
            (await PaidOnOrderAsync(order.OrderId)).ShouldBe(3, detail);
            var paidOnInvoices = await app.ScalarAsync<long>(
                "SELECT coalesce(sum(l.paid), 0) FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id WHERE i.order_id = @o", ("o", order.OrderId));
            paidOnInvoices.ShouldBe(3, detail);
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Final review I1: what is payable on invoice A comes from the order view, which paying invoice B changes while A's own row does
    /// not. A conditional GET of A with its old tag must therefore return the lower payable, not a 304 with the stale one.
    /// </summary>
    [Fact]
    public async Task Paying_another_invoice_changes_the_payable_a_conditional_get_of_this_one_returns()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var order = await app.CreateOrderAsync(tenant);
        await app.ReceiveAsync(order, 3);
        var first = await app.WebhookInvoiceAsync(order.OrderId, "INV-A", 2);
        await app.WaitForStatusAsync(first, "Matched");
        var second = await app.WebhookInvoiceAsync(order.OrderId, "INV-B", 2);
        await app.WaitForStatusAsync(second, "Mismatched");
        var url = $"/api/v1/invoicing/invoices/{first}";
        using var before = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Get, url, token));
        var payableBefore = (await before.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.PayableInclVat;
        var oldTag = before.Headers.ETag!.ToString();
        var (_, secondTag) = await app.GetInvoiceAsync(second, token);
        using var paid = await app.PayAsync(second, token, secondTag);
        paid.StatusCode.ShouldBe(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        InvoiceDto? now = null;
        await InvoicingApp.EventuallyAsync(async () =>
        {
            var request = InvoicingApp.Request(HttpMethod.Get, url, token);
            request.Headers.TryAddWithoutValidation("If-None-Match", oldTag);
            using var again = await app.SendHttpAsync(request);
            if (again.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            now = await again.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json);
            return true;
        }, "a 200 with the new body for the old tag");

        now!.PayableInclVat.ShouldBeLessThan(payableBefore);
    }

    /// <summary>Ruling 15: whoever raised the request cannot release its payment, even with the finance role.</summary>
    [Fact]
    public async Task Requester_cannot_release_payment_403()
    {
        var tenant = InvoicingApp.NewTenant();
        var (_, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var requesterAsFinance = InvoicingApp.Token(tenant, "finance", InvoicingApp.Requester);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, requesterAsFinance);

        using var response = await app.PayAsync(invoiceId, requesterAsFinance, etag);
        using var plainRequester = await app.PayAsync(invoiceId, InvoicingApp.Token(tenant, "requester", "rachel"), etag);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        plainRequester.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "no invoices.release-payment permission");
        (await PaymentsOfAsync(invoiceId)).ShouldBe(0);
    }

    [Fact]
    public async Task Payment_on_held_invoice_422()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (_, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
        var url = $"/api/v1/invoicing/invoices/{invoiceId}";

        using var noIfMatch = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Post, $"{url}/hold", token, body: new { reason = "Waiting for credit note" }));
        using var hold = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Post, $"{url}/hold", token, etag, new { reason = "Waiting for credit note" }));
        hold.StatusCode.ShouldBe(HttpStatusCode.OK, await hold.Content.ReadAsStringAsync());
        var held = (await hold.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!;
        var heldTag = hold.Headers.ETag!.Tag;
        using var stale = await app.PayAsync(invoiceId, token, etag);
        using var onHold = await app.PayAsync(invoiceId, token, heldTag);

        noIfMatch.StatusCode.ShouldBe((HttpStatusCode)428);
        held.OnHold.ShouldBeTrue();
        held.HoldReason.ShouldBe("Waiting for credit note");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, "the hold changed the invoice");
        onHold.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await onHold.Content.ReadAsStringAsync());
        (await PaymentsOfAsync(invoiceId)).ShouldBe(0);

        using var release = await app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Post, $"{url}/release-hold", token, heldTag));
        release.StatusCode.ShouldBe(HttpStatusCode.OK, await release.Content.ReadAsStringAsync());
        using var pay = await app.PayAsync(invoiceId, token, release.Headers.ETag!.Tag);
        pay.StatusCode.ShouldBe(HttpStatusCode.OK, await pay.Content.ReadAsStringAsync());
    }

    /// <summary>A hold and a payment of the same invoice with the same version, at the same time, ten times: exactly one wins each time.</summary>
    [Fact]
    public async Task Hold_racing_payment_one_wins()
    {
        var outcomes = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var tenant = InvoicingApp.NewTenant();
            var token = InvoicingApp.Token(tenant);
            var (_, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
            var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
            var holdDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 10));
            var payDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 10));

            var holding = Task.Run(async () =>
            {
                await Task.Delay(holdDelay);
                return await app.SendHttpAsync(InvoicingApp.Request(
                    HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoiceId}/hold", token, etag, new { reason = "Price query" }));
            });
            var paying = Task.Run(async () =>
            {
                await Task.Delay(payDelay);
                return await app.PayAsync(invoiceId, token, etag);
            });
            using var hold = await holding;
            using var pay = await paying;

            var onHold = await app.ScalarAsync<bool>("SELECT on_hold FROM invoices WHERE id = @id", ("id", invoiceId));
            var payments = await PaymentsOfAsync(invoiceId);
            var detail = $"round {round}: hold {(int)hold.StatusCode}, pay {(int)pay.StatusCode}, on hold {onHold}, payments {payments}";
            output.WriteLine(detail);
            if (pay.StatusCode == HttpStatusCode.OK)
            {
                Lost.ShouldContain(hold.StatusCode, detail);
                onHold.ShouldBeFalse(detail);
                payments.ShouldBe(1, detail);
                outcomes.Add("payment");
            }
            else
            {
                hold.StatusCode.ShouldBe(HttpStatusCode.OK, detail);
                Lost.ShouldContain(pay.StatusCode, detail);
                onHold.ShouldBeTrue(detail);
                payments.ShouldBe(0, detail);
                outcomes.Add("hold");
            }
        }

        output.WriteLine(string.Join(", ", outcomes));
    }

    /// <summary>
    /// The ERP is down when the payment is released: the payment shows the failed attempts and the last error, nothing is lost, and
    /// once the ERP is back the payment is posted once (the ERP knows it by our payment ID) and <c>PaymentExported</c> goes out once.
    /// </summary>
    [Fact]
    public async Task Erp_down_retries_and_exports_once_when_back()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
        var deadBefore = await app.DeadLettersAsync();
        Guid paymentId;
        try
        {
            await app.ErpChaosAsync("Down");
            using var response = await app.PayAsync(invoiceId, token, etag);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            paymentId = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Payments.Single().Id;

            await InvoicingApp.EventuallyAsync(
                async () => await app.ScalarAsync<int>("SELECT export_attempts FROM payments WHERE id = @id", ("id", paymentId)) >= 2, "two failed exports");
            var (pending, _) = await app.GetInvoiceAsync(invoiceId, token);
            var shown = pending.Payments.Single();
            shown.ExportStatus.ShouldBe(ExportStatus.Pending);
            shown.LastExportError.ShouldNotBeNullOrWhiteSpace();
            app.Events.Of(order.RequestId).ShouldNotContain(m => m.Kind == "PaymentExported");
        }
        finally
        {
            await app.ErpChaosAsync("Normal");
        }

        await app.WaitForAsync(order.RequestId, "PaymentExported");
        await InvoicingApp.SettleAsync();

        app.Events.Of(order.RequestId).Count(m => m.Kind == "PaymentExported").ShouldBe(1, "exported once");
        app.Erp.Of(paymentId).Count(c => c.Status is 200 or 201).ShouldBe(1, "posted once");
        app.Erp.Of(paymentId).ShouldContain(c => c.Status == 503);
        (await app.ScalarAsync<string>("SELECT export_status FROM payments WHERE id = @id", ("id", paymentId))).ShouldBe("Exported");
        (await app.ScalarAsync<string>("SELECT last_export_error FROM payments WHERE id = @id", ("id", paymentId))).ShouldBeNull();
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
        app.Metrics().Select(m => m.Name).ShouldContain(InvoicingMetrics.ErpExportFailures);
    }

    /// <summary>
    /// The error rule for a missing order view also covers the ERP export: while the export cannot find the order view, the message is
    /// retried on its schedule (not dead-lettered, not exported); when the view is back the payment is exported.
    /// </summary>
    [Fact]
    public async Task Export_waits_while_the_order_view_is_missing()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
        var deadBefore = await app.DeadLettersAsync();
        Guid paymentId;
        try
        {
            await app.ErpChaosAsync("Down");
            using var response = await app.PayAsync(invoiceId, token, etag);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            paymentId = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Payments.Single().Id;
            await InvoicingApp.EventuallyAsync(
                async () => await app.ScalarAsync<int>("SELECT export_attempts FROM payments WHERE id = @id", ("id", paymentId)) >= 1, "a failed export");

            // The order view "disappears" for the export (it looks the view up in the invoice's tenant).
            await app.ExecuteAsync("UPDATE order_views SET tenant_id = 'elsewhere' WHERE id = @id", ("id", order.OrderId));
        }
        finally
        {
            await app.ErpChaosAsync("Normal");
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        (await app.ScalarAsync<string>("SELECT export_status FROM payments WHERE id = @id", ("id", paymentId))).ShouldBe("Pending");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "retried, not dead-lettered");

        await app.ExecuteAsync("UPDATE order_views SET tenant_id = @t WHERE id = @id", ("t", tenant), ("id", order.OrderId));
        await app.WaitForAsync(order.RequestId, "PaymentExported");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    /// <summary>Ruling 17: pay the 2 received (R 57 960,00), close the third short: the order is settled once, with what was paid.</summary>
    [Fact]
    public async Task Close_short_after_paying_received_settles_once()
    {
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (order, invoiceId) = await InvoicedOrderAsync(tenant, received: 2, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);
        using var pay = await app.PayAsync(invoiceId, token, etag);
        pay.StatusCode.ShouldBe(HttpStatusCode.OK, await pay.Content.ReadAsStringAsync());
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).ShouldNotContain(m => m.Kind == "PurchaseOrderSettled", "one laptop is still outstanding");

        await app.PublishAsync(
            new PurchaseOrderClosedShort(order.OrderId, order.RequestId, tenant, [new ReceivedLine(1, 1)], "Supplier out of stock", "gary", DateTimeOffset.UtcNow),
            tenant);

        var settled = (await app.WaitForAsync(order.RequestId, "PurchaseOrderSettled")).Single(m => m.Kind == "PurchaseOrderSettled");
        settled.Body.GetProperty("paidInclVat").GetDecimal().ShouldBe(57_960m);
        await InvoicingApp.SettleAsync();
        app.Events.Of(order.RequestId).Count(m => m.Kind == "PurchaseOrderSettled").ShouldBe(1);
        (await app.ScalarAsync<DateTime?>("SELECT settled_at FROM order_views WHERE id = @id", ("id", order.OrderId))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cdc_captures_payment_with_audit_row()
    {
        await app.Infra.EnsureDebeziumAsync();
        await using var cdc = await CdcListener.StartAsync(app.Infra.RabbitConnectionString);
        var tenant = InvoicingApp.NewTenant();
        var token = InvoicingApp.Token(tenant);
        var (_, invoiceId) = await InvoicedOrderAsync(tenant, received: 3, invoiced: 3);
        var (_, etag) = await app.GetInvoiceAsync(invoiceId, token);

        using var response = await app.PayAsync(invoiceId, token, etag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paymentId = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!.Payments.Single().Id;

        var payment = await cdc.WaitForAsync(
            m => m.RoutingKey == "invoicingdb.public.payments" && m.Operation == "c" && m.After.GetProperty("id").GetString() == paymentId.ToString(),
            TimeSpan.FromSeconds(30));
        var line = await cdc.WaitForAsync(
            m => m.RoutingKey == "invoicingdb.public.payment_lines" && m.After.GetProperty("payment_id").GetString() == paymentId.ToString(),
            TimeSpan.FromSeconds(30));
        bool InvoiceRow(CdcMessage m) =>
            m.RoutingKey == "invoicingdb.public.invoices" && m.Operation == "u" && m.After.GetProperty("id").GetString() == invoiceId.ToString()
            && m.TransactionId == payment.TransactionId;
        var invoice = cdc.Seen.FirstOrDefault(InvoiceRow) ?? await cdc.WaitForAsync(InvoiceRow, TimeSpan.FromSeconds(30));
        line.TransactionId.ShouldBe(payment.TransactionId);
        invoice.After.GetProperty("status").GetString().ShouldBe(nameof(InvoiceStatus.Paid));
        payment.After.GetProperty("total_incl_vat").GetString().ShouldBe("86940.00");

        await using var db = new InvoicingDbContext(new DbContextOptionsBuilder<InvoicingDbContext>().UseNpgsql(app.Infra.DbConnectionString).Options);
        // The ERP export updates the payment later (export status, ERP reference) in its own transaction, with an audit row of its
        // own. Wait for it, so both rows are always there, and compare the CDC insert with the interceptor's insert row.
        await InvoicingApp.EventuallyAsync(
            async () => await app.ScalarAsync<string>("SELECT export_status FROM payments WHERE id = @id", ("id", paymentId)) == "Exported", "the export");
        var audit = await db.Set<AuditEntry>()
            .SingleAsync(e => e.Table == "payments" && e.PrimaryKey == paymentId.ToString() && e.Operation == AuditOperation.Insert);
        audit.TransactionId.ShouldBe(payment.TransactionId, "the interceptor row and the change share a transaction");
        audit.TenantId.ShouldBe(tenant);
        audit.UserId.ShouldBe(InvoicingApp.Finance);
        cdc.Seen.ShouldNotContain(m => m.RoutingKey.EndsWith(".order_views") || m.RoutingKey.EndsWith(".order_view_lines")
            || m.RoutingKey.EndsWith(".processed_webhooks") || m.RoutingKey.EndsWith(".tenant_sequences"));
    }
}
