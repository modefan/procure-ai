using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Assistant;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Notifications.Application.Live;

/// <summary>
/// Which live updates an integration event causes: one <see cref="EntityChanged"/> per entity it changed, the most specific first and
/// the request last. IDs only (plan 04 ruling 2). The <c>approval</c> entity's ID is the request ID: the approval chain of that request
/// (<c>GET api/v1/approvals/requests/{requestId}</c>); the inbox refetches on any approval push.
/// </summary>
public static class LiveEventMap
{
    /// <summary>The pushes for <paramref name="integrationEvent"/>. An event of another exchange is a programming error.</summary>
    public static IReadOnlyList<EntityChanged> For(IIntegrationEvent integrationEvent) => integrationEvent switch
    {
        // requests
        RequestSubmitted e => Pushes(e, e.RequestId),
        // a cancelled request drops its open approval step from the approver's inbox
        RequestCancelled e => Pushes(e, e.RequestId, Approval(e.RequestId)),

        // approvals: a step asked for, decided or moved changes the approval chain too
        ApprovalRequested e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        ApprovalStepApproved e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        ApprovalEscalated e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        RequestApproved e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        RequestRejected e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        ApprovalReminderDue e => Pushes(e, e.RequestId),
        ApprovalAlertRaised e => Pushes(e, e.RequestId),
        RequestOrdered e => Pushes(e, e.RequestId),
        RequestCompleted e => Pushes(e, e.RequestId),
        RequestCancellationConfirmed e => Pushes(e, e.RequestId, Approval(e.RequestId)),
        RequestCancellationRefused e => Pushes(e, e.RequestId),

        // budgets: the cost center's budget
        BudgetReserved e => Pushes(e, e.RequestId, Budget(e.CostCenterCode)),
        BudgetReservationFailed e => Pushes(e, e.RequestId, Budget(e.CostCenterCode)),
        BudgetReleased e => Pushes(e, e.RequestId, Budget(e.CostCenterCode)),
        BudgetSpent e => Pushes(e, e.RequestId, Budget(e.CostCenterCode)),
        BudgetAmountSet e => [new EntityChanged(LiveEntities.Budget, e.CostCenterCode, null, nameof(BudgetAmountSet), e.OccurredAt)],

        // purchasing: the order (none yet when a request was cancelled before its order was created)
        PurchaseOrderCreated e => Pushes(e, e.RequestId, Order(e.OrderId)),
        PurchaseOrderConfirmed e => Pushes(e, e.RequestId, Order(e.OrderId)),
        PurchaseOrderRejected e => Pushes(e, e.RequestId, Order(e.OrderId)),
        GoodsReceived e => Pushes(e, e.RequestId, Order(e.OrderId)),
        PurchaseOrderClosedShort e => Pushes(e, e.RequestId, Order(e.OrderId)),
        PurchaseOrderCancellationRefused e => Pushes(e, e.RequestId, Order(e.OrderId)),
        PurchaseOrderCancelled e => Pushes(e, e.RequestId, e.OrderId is { } orderId ? Order(orderId) : null),

        // invoicing: the invoice and the order, as far as the event names them
        InvoiceReceived e => Pushes(e, e.RequestId, Invoice(e.InvoiceId), Order(e.OrderId)),
        InvoiceMatched e => Pushes(e, e.RequestId, Invoice(e.InvoiceId), Order(e.OrderId)),
        InvoiceMismatched e => Pushes(e, e.RequestId, Invoice(e.InvoiceId), Order(e.OrderId)),
        PaymentReleased e => Pushes(e, e.RequestId, Invoice(e.InvoiceId), Order(e.OrderId)),
        PaymentExported e => Pushes(e, e.RequestId, Invoice(e.InvoiceId)),
        PurchaseOrderSettled e => Pushes(e, e.RequestId, Order(e.OrderId)),

        // assistant: a policy document's ingestion (no request)
        PolicyDocumentProgressed e => [PolicyDocument(e, e.DocumentId)],
        PolicyDocumentStatusChanged e => [PolicyDocument(e, e.DocumentId)],

        _ => throw new NotSupportedException($"No live updates are defined for {integrationEvent.GetType().FullName}."),
    };

    private static (string Entity, string Id) Approval(Guid requestId) => (LiveEntities.Approval, requestId.ToString());

    private static (string Entity, string Id) Budget(string costCenterCode) => (LiveEntities.Budget, costCenterCode);

    private static (string Entity, string Id) Order(Guid orderId) => (LiveEntities.Order, orderId.ToString());

    private static (string Entity, string Id) Invoice(Guid invoiceId) => (LiveEntities.Invoice, invoiceId.ToString());

    private static EntityChanged PolicyDocument(IIntegrationEvent e, Guid documentId) =>
        new(LiveEntities.PolicyDocument, documentId.ToString(), null, e.GetType().Name, e.OccurredAt);

    /// <summary>The given entities in order (null ones skipped), then the request.</summary>
    private static EntityChanged[] Pushes(IIntegrationEvent e, Guid requestId, params (string Entity, string Id)?[] entities)
    {
        var eventType = e.GetType().Name;
        return
        [
            .. entities.OfType<(string Entity, string Id)>().Select(x => new EntityChanged(x.Entity, x.Id, requestId, eventType, e.OccurredAt)),
            new EntityChanged(LiveEntities.Request, requestId.ToString(), requestId, eventType, e.OccurredAt),
        ];
    }
}
