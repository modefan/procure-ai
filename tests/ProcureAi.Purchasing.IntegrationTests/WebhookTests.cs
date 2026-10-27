using System.Net;
using System.Text;
using Npgsql;
using ProcureAi.Purchasing.Domain;
using Shouldly;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>Review Focus 2: the supplier webhook against forgery, replay and concurrent duplicates.</summary>
[Collection(PurchasingCollection.Name)]
public sealed class WebhookTests(PurchasingApp app)
{
    /// <summary>An order that stays waiting for the supplier (its submissions are refused before reaching the supplier), so no real webhook comes.</summary>
    private async Task<(Guid OrderId, Guid RequestId)> WaitingOrderAsync(string tenant)
    {
        app.Supplier.BlockedTenants[tenant] = true;
        var command = PurchasingApp.NewOrder(tenant);
        return (await app.CreateOrderAsync(command), command.RequestId);
    }

    private async Task<long> ProcessedAsync(string eventId)
    {
        await using var connection = new NpgsqlConnection(app.Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM processed_webhooks WHERE source = 'supplier' AND event_id = @e", connection);
        command.Parameters.AddWithValue("e", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Replayed_webhook_changes_nothing()
    {
        var tenant = PurchasingApp.NewTenant();
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var delivered = app.Webhooks.Of(orderId).Single(d => d.Type == "order.acknowledged");
        var before = (await app.StoredAsync(orderId))!;

        // The same bytes, signed afresh (a replay inside the time window passes the signature check).
        using var replay = await app.PostWebhookAsync(delivered.Body);
        await PurchasingApp.SettleAsync();

        replay.StatusCode.ShouldBe(HttpStatusCode.OK, "a duplicate is answered like a success, so the sender stops");
        var after = (await app.StoredAsync(orderId))!;
        after.Version.ShouldBe(before.Version, "nothing was written to the order");
        app.Events.Of(command.RequestId).Count(m => m.Kind == "PurchaseOrderConfirmed").ShouldBe(1);
        (await ProcessedAsync(delivered.Json.GetProperty("eventId").GetString()!)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_simultaneous_deliveries_of_one_event_confirm_once()
    {
        var tenant = PurchasingApp.NewTenant();
        try
        {
            var (orderId, requestId) = await WaitingOrderAsync(tenant);
            var eventId = Guid.NewGuid().ToString();
            var body = PurchasingApp.Acknowledgement(orderId, "SO-TWIN", eventId);

            var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.PostWebhookAsync(body)));
            await PurchasingApp.SettleAsync();

            responses.Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.OK);
            app.Events.Of(requestId).Count(m => m.Kind == "PurchaseOrderConfirmed").ShouldBe(1, "one effect");
            (await ProcessedAsync(eventId)).ShouldBe(1);
            var stored = (await app.StoredAsync(orderId))!;
            stored.Status.ShouldBe(nameof(OrderStatus.Confirmed));
            stored.SupplierReference.ShouldBe("SO-TWIN");
        }
        finally
        {
            app.Supplier.BlockedTenants.TryRemove(tenant, out _);
        }
    }

    [Fact]
    public async Task Stale_or_forged_webhook_401()
    {
        var tenant = PurchasingApp.NewTenant();
        try
        {
            var (orderId, requestId) = await WaitingOrderAsync(tenant);
            var body = PurchasingApp.Acknowledgement(orderId, "SO-FORGED");

            using var expired = await app.PostWebhookAsync(body, DateTimeOffset.UtcNow.AddMinutes(-6));
            using var wrongSecret = await app.PostWebhookAsync(body, secret: "not-the-supplier-secret-0123456789abcdef");
            using var tamperedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchasing/webhooks/supplier") { Content = new ByteArrayContent(body) };
            WebhookTestSigner.Sign(tamperedRequest, SimulatorHost.Secret, DateTimeOffset.UtcNow);
            tamperedRequest.Content = new StringContent(Encoding.UTF8.GetString(body).Replace("SO-FORGED", "SO-FORGEX"), Encoding.UTF8, "application/json");
            using var tampered = await app.Client.SendAsync(tamperedRequest);
            using var unsigned = await app.Client.PostAsync(
                "/api/v1/purchasing/webhooks/supplier", new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } });
            await PurchasingApp.SettleAsync();

            expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "older than 5 minutes");
            wrongSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "bad signature");
            tampered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "body changed after signing");
            unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no signature header");
            var stored = (await app.StoredAsync(orderId))!;
            stored.Status.ShouldBe(nameof(OrderStatus.PendingSupplier));
            stored.SupplierReference.ShouldBeNull();
            app.Events.Of(requestId).ShouldNotContain(m => m.Kind == "PurchaseOrderConfirmed");
        }
        finally
        {
            app.Supplier.BlockedTenants.TryRemove(tenant, out _);
        }
    }

    [Fact]
    public async Task Webhook_for_unknown_order_200_and_nothing_changes()
    {
        var unknown = Guid.NewGuid();
        var deadBefore = await app.DeadLettersAsync();

        using var response = await app.PostWebhookAsync(PurchasingApp.Acknowledgement(unknown, "SO-NOBODY"));
        using var notOurs = await app.PostWebhookAsync(Encoding.UTF8.GetBytes(
            """{"eventId":"e-not-ours","type":"order.acknowledged","supplierOrderId":"SO-1","externalReference":"someone-else"}"""));
        await PurchasingApp.SettleAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the supplier must not keep sending it");
        notOurs.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.StoredAsync(unknown)).ShouldBeNull();
        app.Events.All.ShouldNotContain(m => m.Text("orderId") == unknown.ToString());
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    /// <summary>
    /// A body that cannot be read is the sender's error (400), checked before anything touches the database. That includes an event ID
    /// longer than the inbox column: a database error there would be a 500, which a sender retries forever.
    /// </summary>
    [Fact]
    public async Task Unreadable_or_oversized_webhook_400()
    {
        var tenant = PurchasingApp.NewTenant();
        try
        {
            var (orderId, _) = await WaitingOrderAsync(tenant);

            using var notJson = await app.PostWebhookAsync(Encoding.UTF8.GetBytes("this is not json"));
            using var noEvent = await app.PostWebhookAsync(Encoding.UTF8.GetBytes(
                $$"""{"type":"order.acknowledged","supplierOrderId":"SO-1","externalReference":"{{orderId}}"}"""));
            using var longEvent = await app.PostWebhookAsync(PurchasingApp.Acknowledgement(orderId, "SO-1", new string('e', 201)));
            using var longReference = await app.PostWebhookAsync(PurchasingApp.Acknowledgement(orderId, new string('S', 101)));
            using var badDate = await app.PostWebhookAsync(Encoding.UTF8.GetBytes(
                $$"""{"eventId":"e-bad-date","type":"order.acknowledged","supplierOrderId":"SO-1","externalReference":"{{orderId}}","expectedDeliveryDate":"soon"}"""));

            notJson.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            noEvent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            longEvent.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an event ID the inbox cannot store");
            longReference.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            badDate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await app.StoredAsync(orderId))!.Status.ShouldBe(nameof(OrderStatus.PendingSupplier));
        }
        finally
        {
            app.Supplier.BlockedTenants.TryRemove(tenant, out _);
        }
    }
}
