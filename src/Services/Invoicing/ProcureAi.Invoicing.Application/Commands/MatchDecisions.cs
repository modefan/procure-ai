using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Application.Commands;

/// <summary>An invoice that was matched (again), and whether the outcome is news.</summary>
/// <param name="WasAwaitingOrder">The invoice had been waiting for its order, so <c>InvoiceReceived</c> has not been published yet.</param>
/// <param name="Changed">Matched or mismatched flipped, or the reasons changed (always true for the first match).</param>
public sealed record MatchOutcome(Invoice Invoice, bool WasAwaitingOrder, bool Changed);

/// <summary>The matching of the invoices of one order as a pure function over the domain, so it can be tested without infrastructure.</summary>
public static class MatchDecisions
{
    /// <summary>
    /// Matches, in arrival order (<see cref="Invoice.Sequence"/>), the invoices of <paramref name="order"/> that still wait for
    /// their order or are matched or mismatched and not on hold. Each is checked against every invoice that arrived before it, paid
    /// or held ones included. Invoices that are partly or fully paid, or held, are left as they are.
    /// With <paramref name="only"/> set, just that invoice is matched (a new invoice does not change the outcome of earlier ones).
    /// The order view is marked as matched when anything was evaluated, so the match and any payment of the order never overlap.
    /// </summary>
    public static IReadOnlyList<MatchOutcome> Rematch(OrderView order, IReadOnlyList<Invoice> invoices, Money tolerance, Invoice? only = null)
    {
        var ofOrder = invoices.Where(i => i.OrderId == order.Id).OrderBy(i => i.Sequence).ToList();
        var outcomes = new List<MatchOutcome>();
        foreach (var invoice in ofOrder)
        {
            if (only is not null && !ReferenceEquals(invoice, only))
            {
                continue;
            }

            var waiting = invoice.Status == InvoiceStatus.AwaitingOrder;
            if (!waiting && (invoice.OnHold || invoice.Status is not (InvoiceStatus.Matched or InvoiceStatus.Mismatched)))
            {
                continue;
            }

            if (waiting)
            {
                invoice.AttachOrder(order);
            }

            var earlier = ofOrder.Where(e => e.Sequence < invoice.Sequence).ToList();
            var result = ThreeWayMatch.Evaluate(invoice, order, earlier, tolerance);
            outcomes.Add(new MatchOutcome(invoice, waiting, invoice.ApplyMatch(result)));
        }

        if (outcomes.Count > 0)
        {
            order.RecordMatch();
        }

        return outcomes;
    }
}
