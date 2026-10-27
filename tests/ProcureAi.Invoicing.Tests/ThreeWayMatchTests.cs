using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

public sealed class ThreeWayMatchTests
{
    [Theory]
    [InlineData(3, 25_200.00, 3, true, null)]
    [InlineData(3, 25_200.00, 2, false, "Line 1: invoiced 3, but only 2 received and not yet invoiced")]
    [InlineData(3, 25_200.40, 3, false, "Line 1: unit price R 25 200,40 differs from the order price R 25 200,00")]
    [InlineData(2, 25_200.25, 2, true, null)]
    [InlineData(2, 25_200.26, 2, false, "Line 1: unit price R 25 200,26 differs from the order price R 25 200,00")]
    public void Invoice_is_checked_against_price_and_received_quantity(
        int quantity, double price, int received, bool match, string? reason)
    {
        var order = Fixture.Order(received);
        var invoice = Fixture.Invoice(quantity, (decimal)price);

        var result = ThreeWayMatch.Evaluate(invoice, order, [], Fixture.Tolerance);

        result.IsMatch.ShouldBe(match);
        if (reason is null)
        {
            result.Reasons.ShouldBeEmpty();
        }
        else
        {
            result.Reasons.ShouldBe([reason]);
        }
    }

    [Fact]
    public void Quantity_counts_what_earlier_invoices_already_claimed()
    {
        var order = Fixture.Order(received: 3);
        var first = Fixture.Invoice(quantity: 2, sequence: 1, number: "INV-1");
        var second = Fixture.Invoice(quantity: 2, sequence: 2, number: "INV-2");

        var result = ThreeWayMatch.Evaluate(second, order, [first], Fixture.Tolerance);

        result.IsMatch.ShouldBeFalse();
        result.Reasons.ShouldBe(["Line 1: invoiced 2, but only 1 received and not yet invoiced"]);
        var line = result.Lines.ShouldHaveSingleItem();
        line.InvoicedBefore.ShouldBe(2);
        line.Received.ShouldBe(3);
        line.Ordered.ShouldBe(3);
        line.QuantityOk.ShouldBeFalse();
        line.PriceOk.ShouldBeTrue();
    }

    [Fact]
    public void The_first_invoice_is_not_held_against_a_later_one()
    {
        var order = Fixture.Order(received: 3);
        var first = Fixture.Invoice(quantity: 2, sequence: 1, number: "INV-1");
        var second = Fixture.Invoice(quantity: 2, sequence: 2, number: "INV-2");

        ThreeWayMatch.Evaluate(first, order, [second], Fixture.Tolerance).IsMatch.ShouldBeTrue();
    }

    [Fact]
    public void Quantity_is_never_reported_below_zero()
    {
        var order = Fixture.Order(received: 1);
        var first = Fixture.Invoice(quantity: 3, sequence: 1, number: "INV-1");
        var second = Fixture.Invoice(quantity: 1, sequence: 2, number: "INV-2");

        ThreeWayMatch.Evaluate(second, order, [first], Fixture.Tolerance).Reasons
            .ShouldBe(["Line 1: invoiced 1, but only 0 received and not yet invoiced"]);
    }

    [Fact]
    public void Another_sku_on_a_line_is_not_on_the_order()
    {
        var result = ThreeWayMatch.Evaluate(Fixture.Invoice(sku: "MON-27"), Fixture.Order(3), [], Fixture.Tolerance);

        result.IsMatch.ShouldBeFalse();
        result.Reasons.ShouldBe(["Line 1 is not on PO-1001"]);
        result.Lines.ShouldHaveSingleItem().PriceOk.ShouldBeFalse();
    }

    [Fact]
    public void A_line_the_order_does_not_have_is_not_on_the_order()
    {
        var invoice = Domain.Invoice.Create(
            "acme", Fixture.OrderId, Guid.NewGuid(), "sup-1", "INV-9", new DateOnly(2026, 10, 27), 1, InvoiceSource.Manual,
            [new InvoiceLine(2, "LAP-14", "Laptop", 1, ProcureAi.BuildingBlocks.Domain.Money.Zar(25_200m))],
            ProcureAi.BuildingBlocks.Domain.Money.Zar(28_980m), "{}", Fixture.Now);

        ThreeWayMatch.Evaluate(invoice, Fixture.Order(3), [], Fixture.Tolerance).Reasons.ShouldBe(["Line 2 is not on PO-1001"]);
    }

    [Fact]
    public void An_invoice_without_a_sku_takes_the_order_line_sku()
    {
        var invoice = Fixture.Invoice(sku: "");

        ThreeWayMatch.Evaluate(invoice, Fixture.Order(3), [], Fixture.Tolerance).IsMatch.ShouldBeTrue();
    }

    [Fact]
    public void Another_supplier_is_reported()
    {
        var result = ThreeWayMatch.Evaluate(Fixture.Invoice(supplier: "sup-2"), Fixture.Order(3), [], Fixture.Tolerance);

        result.IsMatch.ShouldBeFalse();
        result.Reasons.ShouldBe(["Invoice is from sup-2, the order is with sup-1"]);
    }

    [Theory]
    [InlineData(0.50, true)]
    [InlineData(-0.50, true)]
    [InlineData(0.51, false)]
    [InlineData(-0.51, false)]
    public void The_stated_total_may_differ_by_the_tolerance(double difference, bool match)
    {
        var computed = 86_940m;
        var invoice = Fixture.Invoice(stated: computed + (decimal)difference);

        var result = ThreeWayMatch.Evaluate(invoice, Fixture.Order(3), [], Fixture.Tolerance);

        result.IsMatch.ShouldBe(match);
        if (!match)
        {
            result.Reasons.ShouldBe([
                $"Invoice total {ProcureAi.BuildingBlocks.Domain.Money.Zar(computed + (decimal)difference).ToDisplayString()} does not add up to R 86 940,00",
            ]);
        }
    }

    [Fact]
    public void Every_broken_rule_is_reported_in_order()
    {
        var invoice = Fixture.Invoice(quantity: 3, price: 25_201m, supplier: "sup-2", stated: 1m);

        var result = ThreeWayMatch.Evaluate(invoice, Fixture.Order(1), [], Fixture.Tolerance);

        result.Reasons.Count.ShouldBe(4);
        result.Reasons[0].ShouldStartWith("Invoice is from sup-2");
        result.Reasons[1].ShouldStartWith("Line 1: unit price");
        result.Reasons[2].ShouldStartWith("Line 1: invoiced 3, but only 1 received");
        result.Reasons[3].ShouldStartWith("Invoice total R 1,00 does not add up to");
    }
}
