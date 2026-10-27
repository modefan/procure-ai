using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Domain;
using ProcureAi.Purchasing.Infrastructure;
using Shouldly;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>Goods receipt and close short over HTTP (permissions, scope, If-Match, idempotency), and the cancel racing a receipt.</summary>
[Collection(PurchasingCollection.Name)]
public sealed class ReceiptTests(PurchasingApp app, ITestOutputHelper output)
{
    private static readonly HttpStatusCode[] Lost = [HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed, HttpStatusCode.UnprocessableEntity];

    private static string ReceiptsUrl(Guid orderId) => $"/api/v1/purchasing/orders/{orderId}/receipts";

    private Task<HttpResponseMessage> ReceiveAsync(Guid orderId, string token, string? etag, object body, string? idempotencyKey = null) =>
        app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Post, ReceiptsUrl(orderId), token, etag, body, idempotencyKey));

    /// <summary>
    /// Review Focus 1, the most important test: a receipt POST and a <c>CancelPurchaseOrder</c> of the same confirmed order at the
    /// same time, 20 times. Exactly one wins each time: either the receipt (200) and the cancel is refused, or the cancel and the
    /// receipt is turned away (409/412/422). Never both, never neither; nothing is dead-lettered.
    /// </summary>
    [Fact]
    public async Task Cancel_and_receipt_race_exactly_one_wins()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var deadBefore = await app.DeadLettersAsync();
        var outcomes = new List<string>();
        for (var round = 0; round < 20; round++)
        {
            var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
            var (_, etag) = await app.GetOrderAsync(orderId, token);
            // Vary which side starts first, so both outcomes occur.
            var cancelDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 15));
            var receiptDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 15));

            var cancel = Task.Run(async () =>
            {
                await Task.Delay(cancelDelay);
                await app.SendAsync(new CancelPurchaseOrder(command.RequestId, tenant, "Requester cancelled"), tenant);
            });
            var receipt = Task.Run(async () =>
            {
                await Task.Delay(receiptDelay);
                return await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));
            });
            await cancel;
            using var response = await receipt;
            var answers = await app.Events.WaitForAsync(
                command.RequestId, seen => seen.Any(m => m.Kind is "PurchaseOrderCancelled" or "PurchaseOrderCancellationRefused"),
                PurchasingApp.EventTimeout);
            await Task.Delay(300);
            answers = app.Events.Of(command.RequestId);

            var cancelled = answers.Count(m => m.Kind == "PurchaseOrderCancelled");
            var refused = answers.Count(m => m.Kind == "PurchaseOrderCancellationRefused");
            var received = answers.Count(m => m.Kind == "GoodsReceived");
            var stored = (await app.StoredAsync(orderId))!;
            var detail = $"round {round}: receipt {(int)response.StatusCode}, cancelled {cancelled}, refused {refused}, received {received}, stored {stored.Status}/{stored.Receipts}";
            output.WriteLine(detail);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                refused.ShouldBe(1, detail);
                cancelled.ShouldBe(0, detail);
                received.ShouldBe(1, detail);
                stored.Status.ShouldBe(nameof(OrderStatus.PartiallyReceived), detail);
                stored.Receipts.ShouldBe(1, detail);
                outcomes.Add("receipt");
            }
            else
            {
                Lost.ShouldContain(response.StatusCode, detail);
                cancelled.ShouldBe(1, detail);
                refused.ShouldBe(0, detail);
                received.ShouldBe(0, detail);
                stored.Status.ShouldBe(nameof(OrderStatus.Cancelled), detail);
                stored.Receipts.ShouldBe(0, detail);
                outcomes.Add("cancel");
            }
        }

        output.WriteLine($"receipt won {outcomes.Count(o => o == "receipt")}, cancel won {outcomes.Count(o => o == "cancel")}");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "a cancel that lost the race is retried, not dead-lettered");
    }

    /// <summary>
    /// The cancel decides, then pauses before saving (its cache eviction is slowed here) while a receipt commits. Its save then
    /// loses on the order's version (<c>DbUpdateConcurrencyException</c>); the retry rule runs it again, it sees the receipt and
    /// refuses. Without the rule Wolverine would dead-letter the cancel on its first failure and nobody would get an answer.
    /// </summary>
    [Fact]
    public async Task Cancel_that_loses_on_the_version_is_retried_and_refused()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);
        var deadBefore = await app.DeadLettersAsync();
        app.Hooks.SlowEvictions[tenant] = TimeSpan.FromSeconds(2);
        try
        {
            await app.SendAsync(new CancelPurchaseOrder(command.RequestId, tenant, "Requester cancelled"), tenant);
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            using var receipt = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));
            receipt.StatusCode.ShouldBe(HttpStatusCode.OK, "the receipt commits while the cancel waits");
            await app.WaitForAsync(command.RequestId, "PurchaseOrderCancellationRefused");
        }
        finally
        {
            app.Hooks.SlowEvictions.TryRemove(tenant, out _);
        }

        await PurchasingApp.SettleAsync();
        app.Events.Of(command.RequestId).ShouldNotContain(m => m.Kind == "PurchaseOrderCancelled");
        app.Events.Of(command.RequestId).Single(m => m.Kind == "PurchaseOrderCancellationRefused").Text("reason").ShouldBe("Goods have already been received");
        (await app.StoredAsync(orderId))!.Status.ShouldBe(nameof(OrderStatus.PartiallyReceived));
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    [Fact]
    public async Task Receive_more_than_outstanding_422()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);

        using var first = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 2)));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var afterFirst = (await first.Content.ReadFromJsonAsync<PurchaseOrderDto>(PurchasingApp.Json))!;
        using var tooMany = await ReceiveAsync(orderId, token, first.Headers.ETag!.Tag, PurchasingApp.Receipt((1, 2)));

        tooMany.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await tooMany.Content.ReadAsStringAsync());
        afterFirst.Status.ShouldBe(OrderStatus.PartiallyReceived);
        afterFirst.Lines.Single().Outstanding.ShouldBe(1);
        afterFirst.Receipts.ShouldHaveSingleItem().DeliveryNote.ShouldBe("DN-77");
        (await app.StoredAsync(orderId))!.Receipts.ShouldBe(1);
        var received = (await app.WaitForAsync(command.RequestId, "GoodsReceived")).Single(m => m.Kind == "GoodsReceived");
        received.Body.GetProperty("lines")[0].GetProperty("quantity").GetInt32().ShouldBe(2);
        received.Text("receivedBy").ShouldBe("rita");
    }

    [Fact]
    public async Task Receipt_without_if_match_428()
    {
        var tenant = PurchasingApp.NewTenant();
        var (orderId, _) = await app.CreateConfirmedOrderAsync(tenant);

        using var response = await ReceiveAsync(orderId, PurchasingApp.Token(tenant), null, PurchasingApp.Receipt((1, 1)));

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        (await app.StoredAsync(orderId))!.Receipts.ShouldBe(0);
    }

    [Fact]
    public async Task Stale_etag_412()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, _) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);
        using var first = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var stale = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await app.StoredAsync(orderId))!.Receipts.ShouldBe(1);
    }

    [Fact]
    public async Task Ten_parallel_receipts_same_idempotency_key_one_receipt()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)), key)));
        await PurchasingApp.SettleAsync();

        responses.Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        bodies.Distinct().ShouldHaveSingleItem("every duplicate gets the identical response");
        (await app.StoredAsync(orderId))!.Receipts.ShouldBe(1);
        app.Events.Of(command.RequestId).Count(m => m.Kind == "GoodsReceived").ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Two receipts of the same version at once, neither changing the order's status (10 ordered, 1 received before): the order row
    /// itself has no column that changes, yet only one may commit, or the other's line update is lost. PurchasingDbContext writes
    /// the order row whenever its lines or receipts change, so the version check catches it.
    /// </summary>
    [Fact]
    public async Task Concurrent_receipts_of_one_version_one_wins()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, _) = await app.CreateConfirmedOrderAsync(tenant, lines: [("LAP-14", 10, 25_200m)]);
        var (_, etag) = await app.GetOrderAsync(orderId, token);
        using var first = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));
        var version = first.Headers.ETag!.Tag;

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReceiveAsync(orderId, token, version, PurchasingApp.Receipt((1, 2)))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(", ", responses.Select(r => (int)r.StatusCode)));
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.PreconditionFailed || s == HttpStatusCode.Conflict);
        var (order, _) = await app.GetOrderAsync(orderId, token);
        order.Lines.Single().Received.ShouldBe(3);
        order.Receipts.Count.ShouldBe(2);
        (await app.StoredAsync(orderId))!.Receipts.ShouldBe(2);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Requester_sees_own_orders_only_other_cost_center_404()
    {
        var tenant = PurchasingApp.NewTenant();
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant, requester: "rita", costCenter: "CC-200");
        // Everyone the stub knows acts for CC-100 only, unless set otherwise.
        app.Access.SetCostCenters(tenant, "fiona", "CC-200");
        var rita = PurchasingApp.Token(tenant, "requester", "rita");
        var bob = PurchasingApp.Token(tenant, "requester", "bob");
        var fiona = PurchasingApp.Token(tenant, "finance", "fiona");
        var otherTenant = PurchasingApp.Token(PurchasingApp.NewTenant(), "finance", "fiona");

        using var own = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, $"/api/v1/purchasing/orders/{orderId}", rita));
        using var notBobs = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, $"/api/v1/purchasing/orders/{orderId}", bob));
        using var notBobsByRequest = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Get, $"/api/v1/purchasing/requests/{command.RequestId}/order", bob));
        using var bobReceives = await ReceiveAsync(orderId, bob, "\"1\"", PurchasingApp.Receipt((1, 1)));
        using var costCenter = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, $"/api/v1/purchasing/orders/{orderId}", fiona));
        using var foreign = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, $"/api/v1/purchasing/orders/{orderId}", otherTenant));
        using var bobsList = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, "/api/v1/purchasing/orders", bob));
        using var ritasList = await app.SendHttpAsync(PurchasingApp.Request(HttpMethod.Get, "/api/v1/purchasing/orders?status=Confirmed", rita));
        using var anonymous = await app.Client.GetAsync($"/api/v1/purchasing/orders/{orderId}");

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        notBobs.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an order outside the caller's scope looks like one that does not exist");
        notBobsByRequest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        bobReceives.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        costCenter.StatusCode.ShouldBe(HttpStatusCode.OK, "finance acting for the cost center");
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant never sees it");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var bobs = (await bobsList.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        bobs.ShouldBeEmpty();
        var ritas = (await ritasList.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        ritas.Select(o => o.GetProperty("id").GetGuid()).ShouldBe([orderId]);
    }

    [Fact]
    public async Task Close_short_publishes_outstanding_lines()
    {
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, command) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);
        using var tooEarly = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{orderId}/close-short", token, etag, new { reason = "Supplier out of stock" }));
        using var received = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 2)));
        using var shortReason = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{orderId}/close-short", token, received.Headers.ETag!.Tag, new { reason = "no" }));

        using var closed = await app.SendHttpAsync(PurchasingApp.Request(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{orderId}/close-short", token, received.Headers.ETag!.Tag, new { reason = "Supplier out of stock" }));

        tooEarly.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "nothing received yet: cancel instead");
        shortReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the reason needs 3 to 500 characters");
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        var order = (await closed.Content.ReadFromJsonAsync<PurchaseOrderDto>(PurchasingApp.Json))!;
        order.Status.ShouldBe(OrderStatus.ClosedShort);
        order.Lines.Single().ClosedShort.ShouldBe(1);
        var @event = (await app.WaitForAsync(command.RequestId, "PurchaseOrderClosedShort")).Single(m => m.Kind == "PurchaseOrderClosedShort");
        var line = @event.Body.GetProperty("closedLines").EnumerateArray().ShouldHaveSingleItem();
        line.GetProperty("lineNo").GetInt32().ShouldBe(1);
        line.GetProperty("quantity").GetInt32().ShouldBe(1);
        @event.Text("reason").ShouldBe("Supplier out of stock");
        @event.Text("closedBy").ShouldBe("rita");
    }

    [Fact]
    public async Task Cdc_captures_receipt_with_audit_row()
    {
        await app.Infra.EnsureDebeziumAsync();
        await using var cdc = await CdcListener.StartAsync(app.Infra.RabbitConnectionString);
        var tenant = PurchasingApp.NewTenant();
        var token = PurchasingApp.Token(tenant);
        var (orderId, _) = await app.CreateConfirmedOrderAsync(tenant);
        var (_, etag) = await app.GetOrderAsync(orderId, token);

        using var response = await ReceiveAsync(orderId, token, etag, PurchasingApp.Receipt((1, 1)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var receiptId = (await response.Content.ReadFromJsonAsync<PurchaseOrderDto>(PurchasingApp.Json))!.Receipts.Single().Id;

        var receipt = await cdc.WaitForAsync(
            m => m.RoutingKey == "purchasingdb.public.goods_receipts" && m.Operation == "c" && m.After.GetProperty("id").GetString() == receiptId.ToString(),
            TimeSpan.FromSeconds(30));
        var line = await cdc.WaitForAsync(
            m => m.RoutingKey == "purchasingdb.public.receipt_lines" && m.After.GetProperty("receipt_id").GetString() == receiptId.ToString(),
            TimeSpan.FromSeconds(30));
        // The order row is written in the same transaction (its version guards the aggregate); the listener may have passed it already.
        bool OrderRow(CdcMessage m) =>
            m.RoutingKey == "purchasingdb.public.purchase_orders" && m.Operation == "u" && m.After.GetProperty("id").GetString() == orderId.ToString()
            && m.TransactionId == receipt.TransactionId;
        var order = cdc.Seen.FirstOrDefault(OrderRow) ?? await cdc.WaitForAsync(OrderRow, TimeSpan.FromSeconds(30));
        line.TransactionId.ShouldBe(receipt.TransactionId);
        order.After.GetProperty("status").GetString().ShouldBe(nameof(OrderStatus.PartiallyReceived));

        await using var db = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(app.Infra.DbConnectionString).Options);
        var audit = await db.Set<AuditEntry>().FirstAsync(e => e.Table == "goods_receipts" && e.PrimaryKey == receiptId.ToString());
        audit.TransactionId.ShouldBe(receipt.TransactionId, "the interceptor row and the change share a transaction");
        audit.Operation.ShouldBe(AuditOperation.Insert);
        audit.TenantId.ShouldBe(tenant);
        audit.UserId.ShouldBe("rita");
        cdc.Seen.ShouldNotContain(m => m.RoutingKey.EndsWith(".processed_webhooks") || m.RoutingKey.EndsWith(".tenant_sequences"));
    }
}
