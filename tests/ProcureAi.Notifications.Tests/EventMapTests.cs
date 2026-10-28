using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;
using ProcureAi.Notifications.Application.Handlers;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using Shouldly;

namespace ProcureAi.Notifications.Tests;

/// <summary>
/// Every event the live queue receives becomes one push per entity it changed (plan 04 Task 1a): the specific entity first, the request
/// last. IDs only.
/// </summary>
public sealed class EventMapTests
{
    private const string Tenant = "acme";
    private const string CostCenter = "CC-100";
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InvoiceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PaymentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset At = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly string R = RequestId.ToString();
    private static readonly string O = OrderId.ToString();
    private static readonly string I = InvoiceId.ToString();

    private static readonly IReadOnlyList<string> Approvers = ["maria"];

    private static EntityChanged Change(string entity, string id, string eventType) => new(entity, id, RequestId, eventType, At);

    private static EntityChanged[] RequestOnly(string eventType) => [Change(LiveEntities.Request, R, eventType)];

    private static EntityChanged[] ApprovalAndRequest(string eventType) =>
        [Change(LiveEntities.Approval, R, eventType), Change(LiveEntities.Request, R, eventType)];

    private static EntityChanged[] BudgetAndRequest(string eventType) =>
        [Change(LiveEntities.Budget, CostCenter, eventType), Change(LiveEntities.Request, R, eventType)];

    private static EntityChanged[] OrderAndRequest(string eventType) =>
        [Change(LiveEntities.Order, O, eventType), Change(LiveEntities.Request, R, eventType)];

    private static EntityChanged[] InvoiceOrderAndRequest(string eventType) =>
        [Change(LiveEntities.Invoice, I, eventType), Change(LiveEntities.Order, O, eventType), Change(LiveEntities.Request, R, eventType)];

    private static readonly Dictionary<string, (IIntegrationEvent Event, EntityChanged[] Expected)> Cases = new()
    {
        // requests
        ["RequestSubmitted"] = (new RequestSubmitted(RequestId, Tenant, "PR-1", "Laptops", "tom", CostCenter, 100m, At, "sup-1", []), RequestOnly("RequestSubmitted")),
        ["RequestCancelled"] = (new RequestCancelled(RequestId, Tenant, "PR-1", "tom", null, At), RequestOnly("RequestCancelled")),

        // approvals: the request always; the approval chain too where a step was asked for, decided or moved
        ["ApprovalRequested"] = (new ApprovalRequested(RequestId, Tenant, "PR-1", StepId, 1, "Manager", Approvers, At.AddDays(2), At), ApprovalAndRequest("ApprovalRequested")),
        ["ApprovalStepApproved"] = (new ApprovalStepApproved(RequestId, Tenant, StepId, "maria", At), ApprovalAndRequest("ApprovalStepApproved")),
        ["ApprovalEscalated"] = (new ApprovalEscalated(RequestId, Tenant, StepId, "Manager", "Director", Approvers, At), ApprovalAndRequest("ApprovalEscalated")),
        ["RequestApproved"] = (new RequestApproved(RequestId, Tenant, "PR-1", 100m, Approvers, At), ApprovalAndRequest("RequestApproved")),
        ["RequestRejected"] = (new RequestRejected(RequestId, Tenant, "PR-1", "No budget", "maria", At), ApprovalAndRequest("RequestRejected")),
        ["ApprovalReminderDue"] = (new ApprovalReminderDue(RequestId, Tenant, StepId, Approvers, 1, At), RequestOnly("ApprovalReminderDue")),
        ["ApprovalAlertRaised"] = (new ApprovalAlertRaised(RequestId, Tenant, StepId, "Overdue", At), RequestOnly("ApprovalAlertRaised")),
        ["RequestOrdered"] = (new RequestOrdered(RequestId, Tenant, "PR-1", OrderId, "PO-1", At), RequestOnly("RequestOrdered")),
        ["RequestCompleted"] = (new RequestCompleted(RequestId, Tenant, "PR-1", 100m, At), RequestOnly("RequestCompleted")),
        ["RequestCancellationConfirmed"] = (new RequestCancellationConfirmed(RequestId, Tenant, "PR-1", At), RequestOnly("RequestCancellationConfirmed")),
        ["RequestCancellationRefused"] = (new RequestCancellationRefused(RequestId, Tenant, "PR-1", "Delivered", At), RequestOnly("RequestCancellationRefused")),

        // budgets: the cost center's budget (ID = cost center code) and the request
        ["BudgetReserved"] = (new BudgetReserved(RequestId, Tenant, CostCenter, "2026", 100m, At), BudgetAndRequest("BudgetReserved")),
        ["BudgetReservationFailed"] = (new BudgetReservationFailed(RequestId, Tenant, CostCenter, "Insufficient", 100m, 10m, At), BudgetAndRequest("BudgetReservationFailed")),
        ["BudgetReleased"] = (new BudgetReleased(RequestId, Tenant, CostCenter, 100m, At), BudgetAndRequest("BudgetReleased")),
        ["BudgetSpent"] = (new BudgetSpent(RequestId, Tenant, CostCenter, PaymentId, 100m, At), BudgetAndRequest("BudgetSpent")),

        // purchasing: the order and the request
        ["PurchaseOrderCreated"] = (new PurchaseOrderCreated(OrderId, RequestId, Tenant, "PO-1", "PR-1", "sup-1", CostCenter, "tom", [], 100m, 15m, 115m, At), OrderAndRequest("PurchaseOrderCreated")),
        ["PurchaseOrderConfirmed"] = (new PurchaseOrderConfirmed(OrderId, RequestId, Tenant, "PO-1", "S-1", null, At), OrderAndRequest("PurchaseOrderConfirmed")),
        ["PurchaseOrderRejected"] = (new PurchaseOrderRejected(OrderId, RequestId, Tenant, "PO-1", "Out of stock", At), OrderAndRequest("PurchaseOrderRejected")),
        ["GoodsReceived"] = (new GoodsReceived(OrderId, RequestId, Tenant, Guid.NewGuid(), [], "ben", At), OrderAndRequest("GoodsReceived")),
        ["PurchaseOrderClosedShort"] = (new PurchaseOrderClosedShort(OrderId, RequestId, Tenant, [], "Short", "ben", At), OrderAndRequest("PurchaseOrderClosedShort")),
        ["PurchaseOrderCancellationRefused"] = (new PurchaseOrderCancellationRefused(OrderId, RequestId, Tenant, "Shipped", At), OrderAndRequest("PurchaseOrderCancellationRefused")),
        ["PurchaseOrderCancelled"] = (new PurchaseOrderCancelled(OrderId, RequestId, Tenant, "Cancelled", At), OrderAndRequest("PurchaseOrderCancelled")),
        // Cancelled before an order existed: only the request changed.
        ["PurchaseOrderCancelled without order"] = (new PurchaseOrderCancelled(null, RequestId, Tenant, "Cancelled", At), RequestOnly("PurchaseOrderCancelled")),

        // invoicing: the invoice, the order and the request, as far as the event names them
        ["InvoiceReceived"] = (new InvoiceReceived(InvoiceId, OrderId, RequestId, Tenant, "sup-1", "INV-1", "Supplier", 115m, At), InvoiceOrderAndRequest("InvoiceReceived")),
        ["InvoiceMatched"] = (new InvoiceMatched(InvoiceId, OrderId, RequestId, Tenant, At), InvoiceOrderAndRequest("InvoiceMatched")),
        ["InvoiceMismatched"] = (new InvoiceMismatched(InvoiceId, OrderId, RequestId, Tenant, ["Price"], At), InvoiceOrderAndRequest("InvoiceMismatched")),
        ["PaymentReleased"] = (new PaymentReleased(PaymentId, "PAY-1", InvoiceId, OrderId, RequestId, Tenant, 115m, [], "fin", At), InvoiceOrderAndRequest("PaymentReleased")),
        // PaymentExported names no order; PurchaseOrderSettled names no invoice.
        ["PaymentExported"] = (new PaymentExported(PaymentId, InvoiceId, RequestId, Tenant, "ERP-1", At),
            [Change(LiveEntities.Invoice, I, "PaymentExported"), Change(LiveEntities.Request, R, "PaymentExported")]),
        ["PurchaseOrderSettled"] = (new PurchaseOrderSettled(OrderId, RequestId, Tenant, 115m, At), OrderAndRequest("PurchaseOrderSettled")),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Event_becomes_one_push_per_entity_it_changed(string caseName)
    {
        var (integrationEvent, expected) = Cases[caseName];

        LiveEventMap.For(integrationEvent).ShouldBe(expected);
    }

    [Fact]
    public void Every_event_of_the_live_exchanges_has_a_case_here()
    {
        string[] namespaces =
        [
            "ProcureAi.Contracts.Requests", "ProcureAi.Contracts.Approvals", "ProcureAi.Contracts.Budgets",
            "ProcureAi.Contracts.Purchasing", "ProcureAi.Contracts.Invoicing",
        ];
        var events = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => typeof(IIntegrationEvent).IsAssignableFrom(t) && t.IsClass && namespaces.Contains(t.Namespace))
            .ToList();
        var covered = Cases.Values.Select(c => c.Event.GetType()).ToHashSet();

        events.Where(e => !covered.Contains(e)).Select(e => e.Name).ShouldBeEmpty();
    }

    [Fact]
    public void Every_push_names_a_known_entity_and_an_id()
    {
        foreach (var (name, (integrationEvent, _)) in Cases)
        {
            var pushes = LiveEventMap.For(integrationEvent);
            pushes.ShouldNotBeEmpty(name);
            pushes.ShouldAllBe(p => LiveEntities.All.Contains(p.Entity) && !string.IsNullOrEmpty(p.Id), name);
            pushes.Select(p => p.Entity).ShouldBeUnique(name);
        }
    }

    [Fact]
    public async Task Handler_pushes_the_mapped_changes_to_the_events_tenant()
    {
        var pushes = new RecordingPushes();
        var submitted = (RequestSubmitted)Cases["RequestSubmitted"].Event;

        await RequestEventsHandler.Handle(submitted, pushes, TestContext.Current.CancellationToken);

        var sent = pushes.Sent.ShouldHaveSingleItem();
        sent.Tenant.ShouldBe(Tenant);
        sent.Changes.ShouldBe(Cases["RequestSubmitted"].Expected);
    }

    private sealed class RecordingPushes : ILivePushes
    {
        public List<(string Tenant, EntityChanged[] Changes)> Sent { get; } = [];

        public Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken)
        {
            Sent.Add((tenantId, [.. changes]));
            return Task.CompletedTask;
        }

        public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
