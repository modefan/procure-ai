using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Tests;

/// <summary>Builders shared by the tests: an order of 3 x LAP-14 at R 25 200,00 (PO-1001) and invoices against it.</summary>
internal static class Fixture
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    public static readonly Money Tolerance = Money.Zar(0.50m);

    public static readonly Guid OrderId = Guid.Parse("0f2b5f2e-6d7d-4c35-9a53-3f1f4e9e0a01");

    public static OrderView Order(int received = 0, string supplier = "sup-1") =>
        Order([new OrderViewLine(1, "LAP-14", "Laptop", 3, Money.Zar(25_200m))], received, supplier);

    public static OrderView Order(IReadOnlyList<OrderViewLine> lines, int received = 0, string supplier = "sup-1")
    {
        var order = OrderView.Create(OrderId, "acme", Guid.NewGuid(), "PO-1001", supplier, "CC-100", "alice", lines);
        if (received > 0)
        {
            order.ApplyReceipt([(1, received)]);
        }

        return order;
    }

    /// <summary>An invoice with one line 1 (LAP-14); the stated total follows the lines unless given.</summary>
    public static Invoice Invoice(
        int quantity = 3, decimal price = 25_200m, long sequence = 1, string supplier = "sup-1", string sku = "LAP-14",
        decimal? stated = null, string number = "INV-1")
    {
        var lines = new[] { new InvoiceLine(1, sku, "Laptop", quantity, Money.Zar(price)) };
        var total = Money.Zar(price * quantity).InclVat().Amount;
        return Domain.Invoice.Create(
            "acme", OrderId, Guid.NewGuid(), supplier, number, new DateOnly(2026, 10, 27), sequence, InvoiceSource.Manual, lines,
            Money.Zar(stated ?? total), "{}", Now);
    }

    /// <summary>Matches the invoice against the order the way the handlers do and stores the outcome.</summary>
    public static Invoice Matched(Invoice invoice, OrderView order, params Invoice[] earlier)
    {
        invoice.ApplyMatch(ThreeWayMatch.Evaluate(invoice, order, earlier, Tolerance));
        return invoice;
    }
}
