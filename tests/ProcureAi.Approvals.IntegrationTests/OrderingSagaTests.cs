using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The approval saga from the last approval through ordering, cancellation via Purchasing, spend and completion, on real
/// Postgres and RabbitMQ. The test plays Purchasing and Invoicing (it publishes their events) and records what the saga sends.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class OrderingSagaTests(ApprovalsApp app)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyList<RequestLineItem> TwoLines =
    [
        new(1, "LAP-14", "Laptop 14 inch", 2, 12_000m),
        new(2, "DOCK-1", "Docking station", 2, 1_500m),
    ];

    private ApprovalsInstance Main => app.Main;

    private RabbitRecorder Recorder => app.Recorder;

    [Fact]
    public async Task Approval_sends_CreatePurchaseOrder_with_the_submitted_lines()
    {
        var (tenant, submitted) = await ApprovedAsync(lines: TwoLines, supplier: "contoso-hardware");
        var id = submitted.RequestId;

        var create = Recorder.Of(id).Single(m => m.Kind == nameof(CreatePurchaseOrder));
        create.Text("tenantId").ShouldBe(tenant);
        create.Text("requestNumber").ShouldBe(submitted.Number);
        create.Text("requesterId").ShouldBe(DemoUsers.Tom);
        create.Text("costCenterCode").ShouldBe(CostCenter);
        create.Text("supplierId").ShouldBe("contoso-hardware");
        var lines = create.Body.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => $"{l.GetProperty("lineNo").GetInt32()}:{l.GetProperty("sku").GetString()}:{l.GetProperty("quantity").GetInt32()}:{l.GetProperty("unitPriceExclVat").GetDecimal()}")
            .ShouldBe(["1:LAP-14:2:12000", "2:DOCK-1:2:1500"]);
        Recorder.Of(id).Count(m => m.Kind == nameof(RequestApproved)).ShouldBe(1);
        var saga = (await Main.SagaAsync(id))!;
        saga.State.ShouldBe(ApprovalState.Approved);
        saga.SupplierId.ShouldBe("contoso-hardware");
        saga.Lines.Select(l => l.Sku).ShouldBe(["LAP-14", "DOCK-1"]);
    }

    [Fact]
    public async Task Order_created_publishes_RequestOrdered()
    {
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;
        var orderId = Guid.NewGuid();

        await PublishAsync(Created(submitted, orderId, "PO-2001"), Exchanges.Purchasing, tenant);
        var ordered = (await Recorder.WaitForAsync(id, nameof(RequestOrdered), EventTimeout)).Single(m => m.Kind == nameof(RequestOrdered));

        ordered.Text("orderId").ShouldBe(orderId.ToString());
        ordered.Text("orderNumber").ShouldBe("PO-2001");
        ordered.Text("number").ShouldBe(submitted.Number);
        var saga = (await Main.SagaAsync(id))!;
        saga.State.ShouldBe(ApprovalState.Ordered);
        saga.OrderId.ShouldBe(orderId);
        saga.OrderNumber.ShouldBe("PO-2001");

        // The same event delivered again changes nothing.
        await PublishAsync(Created(submitted, orderId, "PO-2001"), Exchanges.Purchasing, tenant);
        await Task.Delay(Settle);
        Recorder.Of(id).Count(m => m.Kind == nameof(RequestOrdered)).ShouldBe(1);
    }

    [Fact]
    public async Task Goods_received_is_remembered()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        await PublishAsync(
            new GoodsReceived(Guid.NewGuid(), id, tenant, Guid.NewGuid(), [new ReceivedLine(1, 1)], DemoUsers.Tom, DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);

        (await Main.WaitForSagaAsync(id, saga => saga is { GoodsReceived: true }, EventTimeout))!.State.ShouldBe(ApprovalState.Ordered);
    }

    [Fact]
    public async Task Supplier_rejection_releases_and_rejects()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        await PublishAsync(
            new PurchaseOrderRejected(Guid.NewGuid(), id, tenant, "PO-2001", "Out of stock", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        var seen = await Recorder.WaitForAsync(id, nameof(RequestRejected), EventTimeout);

        var rejected = seen.Single(m => m.Kind == nameof(RequestRejected));
        rejected.Text("reason").ShouldBe("Supplier rejected the order: Out of stock");
        rejected.Text("rejectedBy").ShouldBeNull();
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        Recorder.Of(id).Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
    }

    [Fact]
    public async Task Supplier_rejection_before_the_order_is_recorded_also_rejects()
    {
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;

        await PublishAsync(
            new PurchaseOrderRejected(Guid.NewGuid(), id, tenant, "PO-2001", "Closed", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestRejected), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        Recorder.Of(id).Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_after_order_sends_CancelPurchaseOrder_then_confirms_and_releases_once()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        var cancel = (await Recorder.WaitForAsync(id, nameof(CancelPurchaseOrder), EventTimeout)).Single(m => m.Kind == nameof(CancelPurchaseOrder));
        cancel.Text("tenantId").ShouldBe(tenant);
        cancel.Text("reason").ShouldBe("No longer needed");
        (await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout))!.StateBeforeCancel.ShouldBe(ApprovalState.Ordered);
        Recorder.Of(id).ShouldNotContain(m => m.Kind == nameof(ReleaseBudget), "Purchasing has not answered yet");

        // The request cancelled again while Purchasing decides asks nothing more.
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        var orderId = (await Main.SagaAsync(id))!.OrderId;
        await PublishAsync(new PurchaseOrderCancelled(orderId, id, tenant, "No longer needed", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCancellationConfirmed), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        // Purchasing's answer delivered again finds no saga.
        await PublishAsync(new PurchaseOrderCancelled(orderId, id, tenant, "No longer needed", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
        seen.Count(m => m.Kind == nameof(RequestCancellationConfirmed)).ShouldBe(1);
        seen.Count(m => m.Kind == nameof(CancelPurchaseOrder)).ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_cancel_reason_is_sent_to_Purchasing_as_a_default(string? reason)
    {
        // Purchasing refuses a blank reason (a DomainException on every attempt); the reason on a request is optional.
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        await PublishAsync(Cancelled(submitted) with { Reason = reason }, Exchanges.Requests, tenant);
        var cancel = (await Recorder.WaitForAsync(id, nameof(CancelPurchaseOrder), EventTimeout)).Single(m => m.Kind == nameof(CancelPurchaseOrder));

        cancel.Text("reason").ShouldBe("Request cancelled");
    }

    [Fact]
    public async Task Cancel_refused_restores_Ordered_and_publishes_refused()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);

        var orderId = (await Main.SagaAsync(id))!.OrderId!.Value;
        await PublishAsync(
            new PurchaseOrderCancellationRefused(orderId, id, tenant, "Goods were already received", DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);
        var refused = (await Recorder.WaitForAsync(id, nameof(RequestCancellationRefused), EventTimeout))
            .Single(m => m.Kind == nameof(RequestCancellationRefused));

        refused.Text("reason").ShouldBe("Goods were already received");
        refused.Text("number").ShouldBe(submitted.Number);
        var saga = await Main.WaitForSagaAsync(id, s => s?.State == ApprovalState.Ordered, EventTimeout);
        saga!.OrderId.ShouldBe(orderId);
        Recorder.Of(id).Where(m => m.Kind is nameof(ReleaseBudget) or nameof(RequestCancellationConfirmed)).ShouldBeEmpty();

        // The restored saga can be cancelled again.
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(CancelPurchaseOrder), EventTimeout, count: 2);
    }

    [Fact]
    public async Task Cancel_while_order_creation_in_flight_ends_cancelled()
    {
        // Approved, the order not recorded yet. Purchasing has not seen CreatePurchaseOrder when the cancel reaches it, so it
        // stores a tombstone and answers PurchaseOrderCancelled with no order.
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;

        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(CancelPurchaseOrder), EventTimeout);
        (await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout))!.StateBeforeCancel.ShouldBe(ApprovalState.Approved);

        await PublishAsync(new PurchaseOrderCancelled(null, id, tenant, "Request cancelled", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCancellationConfirmed), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
        seen.ShouldNotContain(m => m.Kind == nameof(RequestOrdered));
    }

    [Fact]
    public async Task Order_created_while_cancelling_changes_nothing_until_Purchasing_answers()
    {
        // Created first, cancel second at Purchasing: the order exists, so Purchasing cancels it normally; the saga does not
        // announce an order for a request that is being cancelled.
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);

        var orderId = Guid.NewGuid();
        await PublishAsync(Created(submitted, orderId, "PO-2002"), Exchanges.Purchasing, tenant);
        await Task.Delay(Settle);
        (await Main.SagaAsync(id))!.State.ShouldBe(ApprovalState.Cancelling);
        Recorder.Of(id).ShouldNotContain(m => m.Kind == nameof(RequestOrdered));

        await PublishAsync(new PurchaseOrderCancelled(orderId, id, tenant, "Request cancelled", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        Recorder.Of(id).Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
    }

    [Fact]
    public async Task Supplier_rejection_while_cancelling_confirms_the_cancellation()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);

        await PublishAsync(
            new PurchaseOrderRejected(Guid.NewGuid(), id, tenant, "PO-2001", "Closed", DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCancellationConfirmed), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);

        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
        seen.ShouldNotContain(m => m.Kind == nameof(RequestRejected));
    }

    [Fact]
    public async Task Refusal_after_an_order_created_while_cancelling_publishes_RequestOrdered_once()
    {
        // Approved, cancel raised, the order is created meanwhile (not announced), then Purchasing refuses the cancel: Requests
        // must hear of the order, or it stays Approved forever.
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);
        var orderId = Guid.NewGuid();
        await PublishAsync(Created(submitted, orderId, "PO-2003"), Exchanges.Purchasing, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.OrderId == orderId, EventTimeout);

        await PublishAsync(
            new PurchaseOrderCancellationRefused(orderId, id, tenant, "Goods were already received", DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestOrdered), EventTimeout);
        await Task.Delay(Settle);

        var ordered = Recorder.Of(id).Single(m => m.Kind == nameof(RequestOrdered));
        ordered.Text("orderId").ShouldBe(orderId.ToString());
        ordered.Text("orderNumber").ShouldBe("PO-2003");
        Recorder.Of(id).Count(m => m.Kind == nameof(RequestCancellationRefused)).ShouldBe(1);
        (await Main.SagaAsync(id))!.State.ShouldBe(ApprovalState.Ordered);
    }

    [Fact]
    public async Task Settled_while_cancelling_an_ordered_request_refuses_the_cancellation_then_completes()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);

        await PublishAsync(Settled(submitted, 5_000m), Exchanges.Invoicing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCompleted), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        // Purchasing's refusal arrives afterwards and finds no saga.
        await PublishAsync(
            new PurchaseOrderCancellationRefused(Guid.NewGuid(), id, tenant, "Goods were already received", DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);
        await Task.Delay(Settle);

        var kinds = Recorder.Of(id).Select(m => m.Kind).ToList();
        var refused = Recorder.Of(id).Single(m => m.Kind == nameof(RequestCancellationRefused));
        refused.Text("reason").ShouldBe(ApprovalSaga.OrderSettled);
        kinds.IndexOf(nameof(RequestCancellationRefused)).ShouldBeLessThan(kinds.IndexOf(nameof(RequestCompleted)));
        kinds.Count(k => k == nameof(RequestOrdered)).ShouldBe(1, "only the one from ordering");
    }

    [Fact]
    public async Task Settled_while_cancelling_an_unannounced_order_refuses_announces_and_completes()
    {
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;
        await PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);
        var orderId = Guid.NewGuid();
        await PublishAsync(Created(submitted, orderId, "PO-2004"), Exchanges.Purchasing, tenant);
        await Main.WaitForSagaAsync(id, saga => saga?.OrderId == orderId, EventTimeout);

        await PublishAsync(Settled(submitted, 5_000m) with { OrderId = orderId }, Exchanges.Invoicing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCompleted), EventTimeout);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(RequestCancellationRefused)).ShouldBe(1);
        seen.Single(m => m.Kind == nameof(RequestOrdered)).Text("orderNumber").ShouldBe("PO-2004");
    }

    [Fact]
    public async Task Settled_before_the_order_was_recorded_announces_the_order_then_completes()
    {
        var (tenant, submitted) = await ApprovedAsync();
        var id = submitted.RequestId;
        var orderId = Guid.NewGuid();

        await PublishAsync(Settled(submitted, 5_000m) with { OrderId = orderId }, Exchanges.Invoicing, tenant);
        await Recorder.WaitForAsync(id, nameof(RequestCompleted), EventTimeout);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.Single(m => m.Kind == nameof(RequestOrdered)).Text("orderId").ShouldBe(orderId.ToString());
        seen.Select(m => m.Kind).ToList().IndexOf(nameof(RequestOrdered)).ShouldBeLessThan(seen.Select(m => m.Kind).ToList().IndexOf(nameof(RequestCompleted)));
    }

    [Fact]
    public async Task Payment_sends_RecordSpend_even_after_completion()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;
        var first = Guid.NewGuid();

        await PublishAsync(Paid(submitted, first, 11_500m), Exchanges.Invoicing, tenant);
        var spend = (await Recorder.WaitForAsync(id, nameof(RecordSpend), EventTimeout)).Single(m => m.Kind == nameof(RecordSpend));
        spend.Text("paymentId").ShouldBe(first.ToString());
        spend.Text("tenantId").ShouldBe(tenant);
        spend.Body.GetProperty("amount").GetDecimal().ShouldBe(11_500m);

        // Settlement first, then a late payment event (the order of the two events is not guaranteed): the saga is gone and the
        // spend is still recorded, from the NotFound handler.
        await PublishAsync(Settled(submitted, 11_500m), Exchanges.Invoicing, tenant);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        var second = Guid.NewGuid();
        await PublishAsync(Paid(submitted, second, 300m), Exchanges.Invoicing, tenant);
        var seen = await Recorder.WaitForAsync(id, nameof(RecordSpend), EventTimeout, count: 2);

        seen.Where(m => m.Kind == nameof(RecordSpend)).Select(m => m.Text("paymentId")).ShouldBe([first.ToString(), second.ToString()], ignoreOrder: true);
    }

    [Fact]
    public async Task Payment_for_a_request_the_saga_never_saw_still_records_spend()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        var payment = Guid.NewGuid();

        await PublishAsync(
            new PaymentReleased(payment, "PAY-1", Guid.NewGuid(), Guid.NewGuid(), requestId, tenant, 250m, [], DemoUsers.Thandi, DateTimeOffset.UtcNow),
            Exchanges.Invoicing, tenant);

        var spend = (await Recorder.WaitForAsync(requestId, nameof(RecordSpend), EventTimeout)).Single(m => m.Kind == nameof(RecordSpend));
        spend.Text("paymentId").ShouldBe(payment.ToString());
        spend.Body.GetProperty("amount").GetDecimal().ShouldBe(250m);
    }

    [Fact]
    public async Task Settled_releases_leftover_and_completes()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        await PublishAsync(Settled(submitted, 23_000m), Exchanges.Invoicing, tenant);
        var completed = (await Recorder.WaitForAsync(id, nameof(RequestCompleted), EventTimeout)).Single(m => m.Kind == nameof(RequestCompleted));

        completed.Body.GetProperty("spentInclVat").GetDecimal().ShouldBe(23_000m);
        completed.Text("number").ShouldBe(submitted.Number);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        Recorder.Of(id).Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
    }

    [Fact]
    public async Task Duplicate_settled_completes_once()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;

        // Two copies at once (the second save collides with the first and is retried), then a late third.
        await Task.WhenAll(
            PublishAsync(Settled(submitted, 5_000m), Exchanges.Invoicing, tenant),
            PublishAsync(Settled(submitted, 5_000m), Exchanges.Invoicing, tenant));
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await PublishAsync(Settled(submitted, 5_000m), Exchanges.Invoicing, tenant);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(RequestCompleted)).ShouldBe(1);
        seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
    }

    [Fact]
    public async Task Events_the_saga_does_not_need_are_consumed_without_dead_letters()
    {
        var (tenant, submitted) = await OrderedAsync();
        var id = submitted.RequestId;
        var deadBefore = await app.DeadLettersAsync();
        var order = (await Main.SagaAsync(id))!.OrderId!.Value;
        var invoice = Guid.NewGuid();

        await PublishAsync(new PurchaseOrderConfirmed(order, id, tenant, "PO-2001", "SUP-REF", null, DateTimeOffset.UtcNow), Exchanges.Purchasing, tenant);
        await PublishAsync(
            new PurchaseOrderClosedShort(order, id, tenant, [new ReceivedLine(1, 1)], "No more stock", DemoUsers.Tom, DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);
        await PublishAsync(
            new InvoiceReceived(invoice, order, id, tenant, "SUP-1", "INV-1", "Webhook", 100m, DateTimeOffset.UtcNow), Exchanges.Invoicing, tenant);
        await PublishAsync(new InvoiceMatched(invoice, order, id, tenant, DateTimeOffset.UtcNow), Exchanges.Invoicing, tenant);
        await PublishAsync(new InvoiceMismatched(invoice, order, id, tenant, ["Quantity"], DateTimeOffset.UtcNow), Exchanges.Invoicing, tenant);
        await PublishAsync(new PaymentExported(Guid.NewGuid(), invoice, id, tenant, "ERP-1", DateTimeOffset.UtcNow), Exchanges.Invoicing, tenant);
        // A marker behind them: when it has been handled, the events before it have been consumed.
        await PublishAsync(Settled(submitted, 100m), Exchanges.Invoicing, tenant);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);

        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "nothing was moved to the error queue");
        Recorder.Of(id).Count(m => m.Kind == nameof(RequestCompleted)).ShouldBe(1);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private Task PublishAsync(object message, string exchange, string tenant) => Main.PublishAsync(message, exchange, tenant);

    private static PurchaseOrderCreated Created(RequestSubmitted submitted, Guid orderId, string number) =>
        new(orderId, submitted.RequestId, submitted.TenantId, number, submitted.Number, submitted.SupplierId, submitted.CostCenterCode,
            submitted.RequesterId, [], 20_000m, 3_000m, 23_000m, DateTimeOffset.UtcNow);

    private static PaymentReleased Paid(RequestSubmitted submitted, Guid paymentId, decimal amount) =>
        new(paymentId, $"PAY-{paymentId.ToString()[..4]}", Guid.NewGuid(), Guid.NewGuid(), submitted.RequestId, submitted.TenantId, amount, [],
            DemoUsers.Thandi, DateTimeOffset.UtcNow);

    private static PurchaseOrderSettled Settled(RequestSubmitted submitted, decimal paid) =>
        new(Guid.NewGuid(), submitted.RequestId, submitted.TenantId, paid, DateTimeOffset.UtcNow);

    private Task<(string Tenant, RequestSubmitted Submitted)> OrderedAsync() => ApprovedAsync(ordered: true);

    /// <summary>
    /// Submits, reserves and approves (R 5 000: the manager alone) through the real flow, and waits until the saga asked Purchasing
    /// for the order; with <paramref name="ordered"/> also plays Purchasing creating it.
    /// </summary>
    private async Task<(string Tenant, RequestSubmitted Submitted)> ApprovedAsync(
        IReadOnlyList<RequestLineItem>? lines = null, string supplier = "SUP-1", bool ordered = false)
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 5_000m) with { SupplierId = supplier, Lines = lines ?? [new RequestLineItem(1, "PEN", "Pens", 10, 100m)] };
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);
        await Recorder.WaitForAsync(id, nameof(ApprovalRequested), EventTimeout);
        var step = (await Main.StepsAsync(id)).Single();
        await Main.InvokeAsync(new ApproveStep(id, step.Id, step.Version), tenant, DemoUsers.Maria);
        await Recorder.WaitForAsync(id, nameof(RequestApproved), EventTimeout);
        await Recorder.WaitForAsync(id, nameof(CreatePurchaseOrder), EventTimeout);
        if (ordered)
        {
            await PublishAsync(Created(submitted, Guid.NewGuid(), "PO-2001"), Exchanges.Purchasing, tenant);
            await Recorder.WaitForAsync(id, nameof(RequestOrdered), EventTimeout);
        }

        return (tenant, submitted);
    }
}
