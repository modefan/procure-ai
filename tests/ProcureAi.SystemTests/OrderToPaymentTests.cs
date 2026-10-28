using System.Net;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// An approved request all the way through: ordered with the simulated supplier, confirmed by its signed webhook, invoiced by it,
/// received in part, three-way matched, paid in part by finance, exported to the simulated ERP, closed short and completed, with the
/// budget turning reservation into spend. And cancelling an ordered request: before delivery it is cancelled with the supplier and
/// the budget comes back; after a receipt the cancel is refused and the request stays ordered.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class OrderToPaymentTests(SystemFixture system)
{
    // 3 × LAP-14 at R 25 200,00 = R 75 600,00 excl. VAT, R 86 940,00 incl.; two of them R 57 960,00 incl.
    private const decimal LaptopsInclVat = 86_940.00m;
    private const decimal TwoLaptopsInclVat = 57_960.00m;

    // 1 × MON-27 at R 4 200,00 = R 4 830,00 incl. VAT: under R 20 000, so Maria alone approves.
    private const decimal MonitorInclVat = 4_830.00m;

    private readonly SystemFlow _flow = new(system);

    [Fact]
    public async Task Laptops_go_from_request_to_completed()
    {
        system.RequireReady();
        var budgetBefore = await _flow.BudgetAsync();

        // Tom requests three laptops; Maria and the department head approve (over R 20 000).
        var request = await _flow.SubmitAsync("Laptops for the new developers", "LAP-14", 3, LaptopsInclVat);
        await _flow.ApproveAsync(SystemFixture.Maria, request.Id, 1, "Manager", "R 86 940,00");
        await _flow.ApproveAsync(SystemFixture.Dept, request.Id, 2, "DepartmentHead", "R 86 940,00");

        // Ordered as soon as the order exists; the supplier accepts it and confirms by signed webhook.
        var ordered = await _flow.RequestWhenAsync(request.Id, r => r.Status == "Ordered", "request ordered");
        ordered.Body.OrderNumber.ShouldNotBeNull().ShouldMatch(@"^PO-\d{4,}$");
        var (order, _) = await _flow.OrderWhenAsync(request.Id, o => o.Status == "Confirmed", "order confirmed by the supplier's webhook");
        order.Number.ShouldBe(ordered.Body.OrderNumber);
        order.SupplierReference.ShouldNotBeNullOrWhiteSpace();
        order.SupplierStatus.ShouldStartWith("Confirmed");
        order.TotalInclVat.ShouldBe(LaptopsInclVat);

        // The supplier invoices all three by itself; nothing is received yet, so the invoice does not match.
        using var thandi = system.Service("invoicing", SystemFixture.Thandi);
        var invoice = await InvoiceWhenAsync(thandi, order.Id, i => i.Status == "Mismatched", "the supplier's invoice, mismatched");
        invoice.Source.ShouldBe("Webhook");
        invoice.SupplierId.ShouldBe(SystemFlow.Supplier, "the simulator echoes the order's supplier code");
        invoice.Lines.Single().Quantity.ShouldBe(3);
        invoice.Lines.Single().Received.ShouldBe(0);

        // Tom receives two: the invoice is matched again by itself and still does not match (3 invoiced, only 2 received).
        using var tomPurchasing = system.Service("purchasing", SystemFixture.Tom);
        var (_, orderTag) = await tomPurchasing.GetAsync<OrderView>($"/api/v1/purchasing/orders/{order.Id}");
        var (received, _) = await tomPurchasing.SendAsync<OrderView>(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{order.Id}/receipts", HttpStatusCode.OK,
            new { lines = new[] { new { lineNo = 1, quantity = 2 } }, deliveryNote = "DN-1" }, orderTag);
        received.Status.ShouldBe("PartiallyReceived");
        invoice = await InvoiceWhenAsync(thandi, order.Id, i => i.Lines.Single().Received == 2, "invoice re-matched after the receipt");
        invoice.Status.ShouldBe("Mismatched");
        invoice.MismatchReasons.ShouldContain(r => r.Contains("only 2 received"));

        // Thandi (finance) pays what is payable: the two received laptops at the order price, R 57 960,00, exported to the ERP.
        var (_, invoiceTag) = await thandi.GetAsync<InvoiceView>($"/api/v1/invoicing/invoices/{invoice.Id}");
        var (paid, _) = await thandi.SendAsync<InvoiceView>(
            HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoice.Id}/payments", HttpStatusCode.OK, ifMatch: invoiceTag);
        var payment = paid.Payments.ShouldHaveSingleItem();
        payment.TotalInclVat.ShouldBe(TwoLaptopsInclVat);
        payment.ReleasedBy.ShouldBe(SystemFixture.Thandi);
        var exported = (await InvoiceWhenAsync(thandi, order.Id, i => i.Payments.Single().ExportStatus == "Exported", "payment exported")).Payments.Single();
        exported.ErpReference.ShouldNotBeNullOrWhiteSpace();
        using var erp = system.Erp();
        var (posting, _) = await erp.GetAsync<ErpPaymentView>($"/erp/v1/payments/{payment.Id}");
        posting.DocumentNumber.ShouldBe(exported.ErpReference);
        posting.GrossAmountCents.ShouldBe(5_796_000);
        posting.Currency.ShouldBe("ZAR");
        posting.VendorCode.ShouldBe(SystemFlow.Supplier);
        posting.InvoiceNumber.ShouldBe(invoice.SupplierInvoiceNumber);

        // Tom closes the order short by the third laptop: every line is paid or closed, so the request completes.
        var (_, partTag) = await tomPurchasing.GetAsync<OrderView>($"/api/v1/purchasing/orders/{order.Id}");
        var (closed, _) = await tomPurchasing.SendAsync<OrderView>(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{order.Id}/close-short", HttpStatusCode.OK,
            new { reason = "Only two developers started" }, partTag);
        closed.Status.ShouldBe("ClosedShort");
        closed.Lines.Single().ClosedShort.ShouldBe(1);

        var completed = await _flow.RequestWhenAsync(request.Id, r => r.Status == "Completed", "request completed");
        completed.Body.SpentInclVat.ShouldBe(TwoLaptopsInclVat);

        // IT-200: R 57 960,00 more spent; the reservation of R 86 940,00 is gone (spent or released), so reserved is back where it was.
        var budget = await SystemFlow.EventuallyAsync(
            _flow.BudgetAsync,
            b => b.Spent == budgetBefore.Spent + TwoLaptopsInclVat && b.Reserved == budgetBefore.Reserved,
            "IT-200 spent +R 57 960,00, reserved back");
        budget.Available.ShouldBe(budgetBefore.Available - TwoLaptopsInclVat);

        // The audit tells the whole story.
        string[] story =
        [
            "RequestSubmitted", "BudgetReserved", "ApprovalRequested", "ApprovalStepApproved", "RequestApproved",
            "PurchaseOrderCreated", "RequestOrdered", "PurchaseOrderConfirmed", "InvoiceReceived", "InvoiceMismatched", "GoodsReceived",
            "PaymentReleased", "PaymentExported", "PurchaseOrderClosedShort", "PurchaseOrderSettled", "BudgetSpent", "BudgetReleased",
            "RequestCompleted",
        ];
        var history = await _flow.HistoryWhenAsync(request.Id, h => story.All(t => h.Any(e => e.Type == t)), "the whole story in the audit");
        history.Count(e => e.Type == "PurchaseOrderCreated").ShouldBe(1);
        history.Count(e => e.Type == "PurchaseOrderConfirmed").ShouldBe(1);
        history.Count(e => e.Type == "PaymentReleased").ShouldBe(1);
        history.ShouldContain(e => e.Type == "GoodsReceived" && e.UserId == SystemFixture.Tom);
        history.ShouldContain(e => e.Type == "PaymentReleased" && e.UserId == SystemFixture.Thandi && e.Summary.Contains("R 57 960,00"));
        history.ShouldContain(e => e.Type == "PurchaseOrderClosedShort" && e.UserId == SystemFixture.Tom);
        history.ShouldContain(e => e.Type == "BudgetSpent" && e.Summary.StartsWith("R 57 960,00 spent on IT-200"));
        history.ShouldContain(e => e.Type == "RequestCompleted" && e.Summary == "Request completed (R 57 960,00 spent incl. VAT)");

        // Data changes of the request, each matched to the person who caused it.
        var changes = await _flow.ResolvedDataChangesAsync(
            request.Id,
            items => items.Any(c => c.Table == "invoices" && c.UserId == SystemFixture.Thandi)
                     && items.Count(c => c.Table == "purchase_orders" && c.UserId == SystemFixture.Tom) >= 2);
        changes.ShouldAllBe(c => c.Attribution == "App", SystemFlow.Describe(changes));
        changes.ShouldContain(c => c.Table == "purchase_requests" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Dept);
        changes.ShouldContain(c => c.Table == "purchase_orders" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "invoices" && c.UserId == SystemFixture.Thandi);
        await _flow.NoOutsideAppChangesAsync();
    }

    [Fact]
    public async Task Cancel_before_delivery_cancels_with_supplier_and_releases_budget()
    {
        system.RequireReady();
        var before = await _flow.AvailableAsync();
        var request = await OrderedMonitorAsync("Monitor for the meeting room");
        await SystemFlow.EventuallyAsync(_flow.AvailableAsync, a => a == before - MonitorInclVat, "budget reserved");

        // Tom cancels before anything arrived: Purchasing cancels the order and tells the supplier; the request ends Cancelled.
        using var tom = system.Service("requests", SystemFixture.Tom);
        var (_, tag) = await tom.GetAsync<RequestView>($"/api/v1/requests/{request.Id}");
        var (cancelling, _) = await tom.SendAsync<RequestView>(
            HttpMethod.Post, $"/api/v1/requests/{request.Id}/cancel", HttpStatusCode.OK, new { reason = "Meeting room moved" }, tag);
        cancelling.Status.ShouldBeOneOf("Cancelling", "Cancelled");

        await _flow.RequestWhenAsync(request.Id, r => r.Status == "Cancelled", "request cancelled");
        var (order, _) = await _flow.OrderWhenAsync(request.Id, o => o.Status == "Cancelled" && o.SupplierStatus == "Cancelled", "order cancelled with the supplier");
        order.Lines.Single().Received.ShouldBe(0);
        (await SystemFlow.EventuallyAsync(_flow.AvailableAsync, a => a == before, "budget released")).ShouldBe(before);

        var history = await _flow.HistoryWhenAsync(
            request.Id,
            h => h.Any(e => e.Type == "PurchaseOrderCancelled") && h.Any(e => e.Type == "RequestCancellationConfirmed") && h.Any(e => e.Type == "BudgetReleased"),
            "cancellation in the audit");
        history.Count(e => e.Type == "BudgetReleased").ShouldBe(1);
        history.ShouldContain(e => e.Type == "RequestCancelled" && e.UserId == SystemFixture.Tom);
        history.ShouldNotContain(e => e.Type == "PurchaseOrderCancellationRefused");
    }

    [Fact]
    public async Task Cancel_after_receipt_is_refused_and_request_stays_ordered()
    {
        system.RequireReady();
        var before = await _flow.AvailableAsync();
        var request = await OrderedMonitorAsync("Monitor for the help desk");
        var (order, orderTag) = await _flow.OrderWhenAsync(request.Id, o => o.Status == "Confirmed", "order confirmed");

        // The monitor arrives; then Tom tries to cancel. Goods were received, so Purchasing refuses and the request goes back to Ordered.
        using var tomPurchasing = system.Service("purchasing", SystemFixture.Tom);
        await tomPurchasing.SendAsync<OrderView>(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{order.Id}/receipts", HttpStatusCode.OK,
            new { lines = new[] { new { lineNo = 1, quantity = 1 } }, deliveryNote = (string?)null }, orderTag);
        using var tom = system.Service("requests", SystemFixture.Tom);
        var (_, tag) = await tom.GetAsync<RequestView>($"/api/v1/requests/{request.Id}");
        await tom.SendAsync<RequestView>(HttpMethod.Post, $"/api/v1/requests/{request.Id}/cancel", HttpStatusCode.OK, new { reason = "Not needed after all" }, tag);

        var refused = await _flow.RequestWhenAsync(request.Id, r => r.Status == "Ordered" && r.CancellationRefusedReason is not null, "cancel refused, request ordered");
        refused.Body.CancellationRefusedReason.ShouldNotBeNullOrWhiteSpace();
        var (after, _) = await _flow.OrderWhenAsync(request.Id, _ => true, "order");
        after.Status.ShouldBe("Received");
        // The reservation stays: the monitor will be invoiced and paid.
        (await _flow.AvailableAsync()).ShouldBe(before - MonitorInclVat);

        var history = await _flow.HistoryWhenAsync(
            request.Id, h => h.Any(e => e.Type == "PurchaseOrderCancellationRefused") && h.Any(e => e.Type == "RequestCancellationRefused"), "refusal in the audit");
        history.ShouldNotContain(e => e.Type == "PurchaseOrderCancelled");
        history.ShouldNotContain(e => e.Type == "BudgetReleased");
    }

    /// <summary>Tom requests one monitor, Maria approves, and the request is ordered.</summary>
    private async Task<RequestView> OrderedMonitorAsync(string title)
    {
        var request = await _flow.SubmitAsync(title, "MON-27", 1, MonitorInclVat);
        await _flow.ApproveAsync(SystemFixture.Maria, request.Id, 1, "Manager", "R 4 830,00");
        return (await _flow.RequestWhenAsync(request.Id, r => r.Status == "Ordered", "request ordered")).Body;
    }

    private static async Task<InvoiceView> InvoiceWhenAsync(ServiceClient invoicing, Guid orderId, Func<InvoiceView, bool> done, string what) =>
        (await SystemFlow.EventuallyAsync(
            () => invoicing.GetAsync<Paged<InvoiceView>>($"/api/v1/invoicing/invoices?orderId={orderId}"),
            r => r.Body.Items.Count == 1 && done(r.Body.Items[0]),
            what)).Body.Items[0];
}
