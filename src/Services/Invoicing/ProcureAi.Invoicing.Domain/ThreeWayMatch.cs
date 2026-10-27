using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>What one invoice line was checked against: price, and quantity against what was received and not yet invoiced.</summary>
public sealed record LineMatch(int LineNo, bool PriceOk, bool QuantityOk, int Ordered, int Received, int InvoicedBefore, string? Reason);

public sealed record MatchResult(bool IsMatch, IReadOnlyList<LineMatch> Lines, IReadOnlyList<string> Reasons);

/// <summary>
/// The cumulative three-way match (order, delivery, invoice) as a pure function. Invoices of one order are matched in arrival
/// order: a line fits when the quantity on earlier invoices plus this one is at most what was received.
/// </summary>
public static class ThreeWayMatch
{
    public static MatchResult Evaluate(Invoice invoice, OrderView order, IReadOnlyList<Invoice> earlierInvoices, Money tolerance)
    {
        var reasons = new List<string>();
        var lines = new List<LineMatch>();

        if (!string.Equals(invoice.SupplierId, order.SupplierId, StringComparison.Ordinal))
        {
            reasons.Add($"Invoice is from {invoice.SupplierId}, the order is with {order.SupplierId}");
        }

        var earlier = earlierInvoices
            .Where(i => i.Id != invoice.Id && i.OrderId == invoice.OrderId && i.Sequence < invoice.Sequence)
            .ToList();

        foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
        {
            var orderLine = order.Lines.FirstOrDefault(o => o.LineNo == line.LineNo);
            if (orderLine is null || (line.Sku.Length > 0 && !string.Equals(orderLine.Sku, line.Sku, StringComparison.OrdinalIgnoreCase)))
            {
                var missing = $"Line {line.LineNo} is not on {order.OrderNumber}";
                reasons.Add(missing);
                lines.Add(new LineMatch(line.LineNo, false, false, orderLine?.Ordered ?? 0, orderLine?.Received ?? 0, 0, missing));
                continue;
            }

            var lineReasons = new List<string>();
            var invoicedBefore = earlier.SelectMany(i => i.Lines).Where(l => l.LineNo == line.LineNo).Sum(l => l.Quantity);

            var difference = Math.Abs(((line.UnitPriceExclVat * line.Quantity) - (orderLine.UnitPriceExclVat * line.Quantity)).Amount);
            var priceOk = difference <= tolerance.Amount;
            if (!priceOk)
            {
                lineReasons.Add(
                    $"Line {line.LineNo}: unit price {line.UnitPriceExclVat.ToDisplayString()} differs from the order price {orderLine.UnitPriceExclVat.ToDisplayString()}");
            }

            var quantityOk = invoicedBefore + line.Quantity <= orderLine.Received;
            if (!quantityOk)
            {
                lineReasons.Add(
                    $"Line {line.LineNo}: invoiced {line.Quantity}, but only {Math.Max(0, orderLine.Received - invoicedBefore)} received and not yet invoiced");
            }

            reasons.AddRange(lineReasons);
            lines.Add(new LineMatch(
                line.LineNo, priceOk, quantityOk, orderLine.Ordered, orderLine.Received, invoicedBefore,
                lineReasons.Count == 0 ? null : string.Join("; ", lineReasons)));
        }

        if (Math.Abs(invoice.StatedTotalInclVat.Amount - invoice.TotalInclVat.Amount) > tolerance.Amount)
        {
            reasons.Add($"Invoice total {invoice.StatedTotalInclVat.ToDisplayString()} does not add up to {invoice.TotalInclVat.ToDisplayString()}");
        }

        return new MatchResult(reasons.Count == 0, lines, reasons);
    }
}
