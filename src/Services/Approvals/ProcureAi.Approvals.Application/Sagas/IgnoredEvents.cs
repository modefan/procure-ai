using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Purchasing;

namespace ProcureAi.Approvals.Application.Sagas;

/// <summary>
/// Events that reach the Approvals queues (every event of an exchange does) but need nothing here. Handled explicitly so they
/// are consumed, not reported as messages without a handler.
/// </summary>
public static class IgnoredEventsHandler
{
    /// <summary>The release the saga asked for happened; the saga already moved on when it asked.</summary>
    public static void Handle(BudgetReleased released)
    {
    }

    /// <summary>Spend recorded on a payment; the saga learns of completion from Invoicing.</summary>
    public static void Handle(BudgetSpent spent)
    {
    }

    public static void Handle(PurchaseOrderConfirmed confirmed)
    {
    }

    public static void Handle(PurchaseOrderClosedShort closedShort)
    {
    }

    public static void Handle(InvoiceReceived received)
    {
    }

    public static void Handle(InvoiceMatched matched)
    {
    }

    public static void Handle(InvoiceMismatched mismatched)
    {
    }

    public static void Handle(PaymentExported exported)
    {
    }
}
