using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Domain;
using ProcureAi.Purchasing.Infrastructure;
using Shouldly;
using Wolverine;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>Creating an order, sending it through the supplier ACL, the supplier's answers, the outage, and cancelling.</summary>
[Collection(PurchasingCollection.Name)]
public sealed class OrderFlowTests(PurchasingApp app)
{
    [Fact]
    public async Task CreatePurchaseOrder_creates_PO_1001_publishes_created_and_sends_to_supplier()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);

        await app.SendAsync(command, tenant);

        var created = (await app.WaitForAsync(command.RequestId, "PurchaseOrderCreated")).Single(m => m.Kind == "PurchaseOrderCreated");
        created.Text("orderNumber").ShouldBe("PO-1001", "the first order of a tenant");
        created.Text("tenantId").ShouldBe(tenant);
        created.Body.GetProperty("totalExclVat").GetDecimal().ShouldBe(75_600.00m);
        created.Body.GetProperty("vat").GetDecimal().ShouldBe(11_340.00m);
        created.Body.GetProperty("totalInclVat").GetDecimal().ShouldBe(86_940.00m);
        var orderId = Guid.Parse(created.Text("orderId")!);

        await app.WaitForAsync(command.RequestId, "PurchaseOrderConfirmed");
        var submission = app.Supplier.SubmissionsOf(orderId).ShouldHaveSingleItem("sent once, in the supplier's vocabulary");
        submission.Status.ShouldBe(202);
        submission.BuyerReference.ShouldBe($"{tenant}/PO-1001");
        submission.Body!.Value.GetProperty("supplierCode").GetString().ShouldBe(command.SupplierId, "the order's supplier, quoted on its invoices");
        app.Simulator.Store.Find(submission.SupplierOrderId!)!.SupplierCode.ShouldBe(command.SupplierId);
        var line = submission.Body!.Value.GetProperty("lines")[0];
        line.GetProperty("lineNumber").GetInt32().ShouldBe(1);
        line.GetProperty("articleNumber").GetString().ShouldBe("LAP-14");
        line.GetProperty("quantity").GetInt32().ShouldBe(3);
        line.GetProperty("netPriceCents").GetInt64().ShouldBe(2_520_000, "R 25 200,00 in cents");
        submission.Body!.Value.GetProperty("currency").GetString().ShouldBe("ZAR");

        var stored = (await app.StoredAsync(orderId))!;
        stored.Status.ShouldBe(nameof(OrderStatus.Confirmed));
        stored.Number.ShouldBe(1001);
        stored.SupplierReference.ShouldBe(submission.SupplierOrderId);
    }

    /// <summary>
    /// Two copies of one create race: both find no order (the order numbers are handed out slowly here), both insert, and the loser
    /// hits the unique (tenant, request) index. Its retry (the unique-violation rule) then finds the order and does nothing.
    /// Without the rule the loser would fail (and, from the queue, go to the error queue on its first failure).
    /// </summary>
    [Fact]
    public async Task Duplicate_CreatePurchaseOrder_creates_one_order()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);
        app.Hooks.SlowSequences[tenant] = TimeSpan.FromMilliseconds(500);
        var deadBefore = await app.DeadLettersAsync();
        try
        {
            await Task.WhenAll(app.Bus.InvokeAsync(command), app.Bus.InvokeAsync(command));
            await app.SendAsync(command, tenant);
            await app.SendAsync(command, tenant);
            await app.WaitForAsync(command.RequestId, "PurchaseOrderCreated");
            await PurchasingApp.SettleAsync();
        }
        finally
        {
            app.Hooks.SlowSequences.TryRemove(tenant, out _);
        }

        (await app.OrderRowsAsync(tenant, command.RequestId)).ShouldBe(1);
        app.Events.Of(command.RequestId).Count(m => m.Kind == "PurchaseOrderCreated").ShouldBe(1);
        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "the losing create is retried, not dead-lettered");
    }

    [Fact]
    public async Task Signed_acknowledgement_confirms_and_publishes_confirmed()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);
        var orderId = await app.CreateOrderAsync(command);

        var confirmed = (await app.WaitForAsync(command.RequestId, "PurchaseOrderConfirmed")).Single(m => m.Kind == "PurchaseOrderConfirmed");

        var delivery = app.Webhooks.Of(orderId).ShouldHaveSingleItem("the simulator's signed acknowledgement");
        delivery.Type.ShouldBe("order.acknowledged");
        delivery.Status.ShouldBe(200);
        confirmed.Text("supplierReference").ShouldBe(delivery.Json.GetProperty("supplierOrderId").GetString());
        confirmed.Text("expectedDelivery").ShouldBe(delivery.Json.GetProperty("expectedDeliveryDate").GetString());
        confirmed.Text("orderNumber").ShouldBe("PO-1001");
        var (order, _) = await app.GetOrderAsync(orderId, PurchasingApp.Token(tenant));
        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.SupplierStatus.ShouldStartWith("Confirmed, delivery ");
    }

    /// <summary>
    /// Review Focus 4: the supplier's acknowledgement arrives while our own "sent" commit is still pending (the 202 is held here
    /// until the webhook has confirmed the order). The send handler then loses on the version, reads the order again, finds it
    /// confirmed and leaves it: confirmed once, never moved back to "sent".
    /// </summary>
    [Fact]
    public async Task Confirmation_before_sent_commit_still_confirms()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);
        var held = new TaskCompletionSource();
        // Hold the 202 of the submit until the supplier's webhook has confirmed the order.
        app.Supplier.HoldAfterAccepted[tenant] = async id =>
        {
            held.TrySetResult();
            await PurchasingApp.EventuallyAsync(
                async () => (await app.StoredAsync(id))?.Status == nameof(OrderStatus.Confirmed), "the webhook to confirm first", TimeSpan.FromSeconds(4));
        };
        var orderId = await app.CreateOrderAsync(command);

        await app.WaitForAsync(command.RequestId, "PurchaseOrderConfirmed");
        await PurchasingApp.SettleAsync();

        held.Task.IsCompleted.ShouldBeTrue("the 202 was held until the webhook had confirmed the order");
        app.Events.Of(command.RequestId).Count(m => m.Kind == "PurchaseOrderConfirmed").ShouldBe(1);
        var stored = (await app.StoredAsync(orderId))!;
        stored.Status.ShouldBe(nameof(OrderStatus.Confirmed), "the late 'sent' does not move it back");
        stored.SupplierReference.ShouldBe(app.Supplier.SubmissionsOf(orderId).Single().SupplierOrderId);
    }

    /// <summary>
    /// Review Focus 4: the supplier is down. Every send fails, the circuit opens, the order shows its attempts and the last error,
    /// nothing is lost; once the supplier is back the order is sent and confirmed exactly once. The circuit's events and the send
    /// failures reach the service's metric exporters.
    /// </summary>
    [Fact]
    public async Task Supplier_down_opens_circuit_records_attempts_and_recovers_once()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);
        var circuitOpened = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Polly")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "event.name", Value: "OnCircuitOpened" })
                {
                    Interlocked.Increment(ref circuitOpened);
                }
            }
        });
        listener.Start();

        await app.ChaosAsync("Down");
        Guid orderId;
        try
        {
            orderId = await app.CreateOrderAsync(command);
            await PurchasingApp.EventuallyAsync(
                async () => (await app.StoredAsync(orderId))!.SupplierAttempts >= 3, "three failed attempts", TimeSpan.FromSeconds(45));

            var stored = (await app.StoredAsync(orderId))!;
            stored.Status.ShouldBe(nameof(OrderStatus.PendingSupplier));
            stored.LastSupplierError.ShouldNotBeNullOrWhiteSpace();
            var (order, _) = await app.GetOrderAsync(orderId, PurchasingApp.Token(tenant));
            order.SupplierStatus.ShouldStartWith("Supplier unavailable · retrying");
            Volatile.Read(ref circuitOpened).ShouldBeGreaterThan(0, "the supplier circuit opened");
            app.Supplier.SubmissionsOf(orderId).ShouldAllBe(c => c.Status == 503);
            await PurchasingApp.EventuallyAsync(
                () => Task.FromResult(app.Services.GetRequiredService<PurchasingMetrics>().Waiting > 0), "the waiting-orders gauge", TimeSpan.FromSeconds(10));
        }
        finally
        {
            await app.ChaosAsync("Normal");
        }

        await app.WaitForAsync(command.RequestId, "PurchaseOrderConfirmed");
        await PurchasingApp.SettleAsync();

        app.Events.Of(command.RequestId).Count(m => m.Kind == "PurchaseOrderConfirmed").ShouldBe(1, "confirmed once");
        app.Supplier.SubmissionsOf(orderId).Where(c => c.Status == 202).Select(c => c.SupplierOrderId).Distinct().ShouldHaveSingleItem("one supplier order");
        app.Webhooks.Of(orderId).Count(d => d.Type == "order.acknowledged").ShouldBe(1);
        (await app.StoredAsync(orderId))!.Status.ShouldBe(nameof(OrderStatus.Confirmed));

        app.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();
        List<MetricSnapshot> exported;
        lock (app.ExportedMetrics)
        {
            exported = [.. app.ExportedMetrics];
        }

        exported.Select(m => m.Name).ShouldContain("resilience.polly.strategy.events", "Polly's metrics reach the exporters");
        exported.Select(m => m.Name).ShouldContain(PurchasingMetrics.SupplierSendFailures);
        exported.Select(m => m.Name).ShouldContain(PurchasingMetrics.OrdersWaitingForSupplier);
    }

    [Fact]
    public async Task Discontinued_article_rejected_publishes_rejected()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant, lines: [("PEN-01", 2, 10m), ("DISC-01", 1, 9m)]);
        var orderId = await app.CreateOrderAsync(command);

        var rejected = (await app.WaitForAsync(command.RequestId, "PurchaseOrderRejected")).Single(m => m.Kind == "PurchaseOrderRejected");

        rejected.Text("reason").ShouldBe("Article DISC-01 is discontinued");
        rejected.Text("orderNumber").ShouldBe("PO-1001");
        var stored = (await app.StoredAsync(orderId))!;
        stored.Status.ShouldBe(nameof(OrderStatus.Rejected));
        var (order, _) = await app.GetOrderAsync(orderId, PurchasingApp.Token(tenant));
        order.SupplierStatus.ShouldBe("Rejected by supplier: Article DISC-01 is discontinued");
    }

    [Fact]
    public async Task Cancel_before_create_leaves_tombstone_and_create_does_nothing()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);

        await app.SendAsync(new CancelPurchaseOrder(command.RequestId, tenant, "Requester cancelled"), tenant);
        var cancelled = (await app.WaitForAsync(command.RequestId, "PurchaseOrderCancelled")).Single();
        await app.SendAsync(command, tenant);
        await PurchasingApp.SettleAsync();

        cancelled.Text("orderId").ShouldBeNull("there was no order yet");
        app.Events.Of(command.RequestId).ShouldNotContain(m => m.Kind == "PurchaseOrderCreated");
        (await app.OrderRowsAsync(tenant, command.RequestId)).ShouldBe(1, "only the tombstone");
        using var forRequest = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Get, $"/api/v1/purchasing/requests/{command.RequestId}/order", PurchasingApp.Token(tenant)));
        forRequest.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a tombstone is not an order");
        app.Supplier.Calls.ShouldNotContain(c => c.BuyerReference != null && c.BuyerReference.StartsWith(tenant + "/"));
    }

    [Fact]
    public async Task Cancel_confirmed_order_tells_supplier()
    {
        var tenant = PurchasingApp.NewTenant();
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var reference = (await app.StoredAsync(orderId))!.SupplierReference!;

        await app.SendAsync(new CancelPurchaseOrder(command.RequestId, tenant, "Requester cancelled"), tenant);

        var cancelled = (await app.WaitForAsync(command.RequestId, "PurchaseOrderCancelled")).Single(m => m.Kind == "PurchaseOrderCancelled");
        cancelled.Text("orderId").ShouldBe(orderId.ToString());
        await PurchasingApp.EventuallyAsync(async () => !(await app.StoredAsync(orderId))!.SupplierCancelPending, "the supplier to be told");
        app.Simulator.Store.Find(reference)!.Cancelled.ShouldBeTrue();
        app.Supplier.CancelsOf(reference).Count(c => c.Status == 200).ShouldBe(1);
        (await app.StoredAsync(orderId))!.Status.ShouldBe(nameof(OrderStatus.Cancelled));
    }

    /// <summary>
    /// A submit that reached the supplier but whose answer never came back (here: the order is placed at the supplier directly),
    /// then a cancel: our order has no supplier reference, so nobody tells the supplier. Its acknowledgement then names the order:
    /// the reference is recorded and the supplier is told to cancel, exactly once, also when the acknowledgement comes again.
    /// </summary>
    [Fact]
    public async Task Confirmation_after_cancel_without_reference_cancels_at_supplier()
    {
        var tenant = PurchasingApp.NewTenant();
        var command = PurchasingApp.NewOrder(tenant);
        app.Supplier.BlockedTenants[tenant] = true;
        Guid orderId;
        try
        {
            orderId = await app.CreateOrderAsync(command);
            await PurchasingApp.EventuallyAsync(async () => (await app.StoredAsync(orderId))!.SupplierAttempts >= 1, "a failed attempt");
            await app.SendAsync(new CancelPurchaseOrder(command.RequestId, tenant, "Requester cancelled"), tenant);
            await app.WaitForAsync(command.RequestId, "PurchaseOrderCancelled");
        }
        finally
        {
            app.Supplier.BlockedTenants.TryRemove(tenant, out _);
        }

        (await app.StoredAsync(orderId))!.SupplierReference.ShouldBeNull();
        using var supplier = app.Simulator.CreateClient();
        supplier.DefaultRequestHeaders.Add("X-Api-Key", SimulatorHost.SimulatorKey);
        using var placed = await supplier.PostAsJsonAsync("supplier/v1/orders", new
        {
            externalReference = orderId.ToString(),
            buyerReference = $"{tenant}/PO-1001",
            currency = "ZAR",
            lines = new[] { new { lineNumber = 1, articleNumber = "LAP-14", quantity = 3, netPriceCents = 2_520_000L } },
        });
        placed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await placed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("supplierOrderId").GetString()!;

        await PurchasingApp.EventuallyAsync(() => Task.FromResult(app.Simulator.Store.Find(reference)!.Cancelled), "the supplier to be told");
        var acknowledgement = app.Webhooks.Of(orderId).Single(d => d.Type == "order.acknowledged");
        using var again = await app.PostWebhookAsync(PurchasingApp.Acknowledgement(orderId, reference));
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PurchasingApp.SettleAsync();

        acknowledgement.Status.ShouldBe(200);
        app.Supplier.CancelsOf(reference).ShouldHaveSingleItem("exactly one supplier cancel call").Status.ShouldBe(200);
        var stored = (await app.StoredAsync(orderId))!;
        stored.Status.ShouldBe(nameof(OrderStatus.Cancelled));
        stored.SupplierReference.ShouldBe(reference);
        stored.SupplierCancelPending.ShouldBeFalse();
        app.Events.Of(command.RequestId).ShouldNotContain(m => m.Kind == "PurchaseOrderConfirmed");
    }
}
