using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Application.Mapping;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

/// <summary>What a "pay all payable" click would pay is on the DTO, so screens never work it out themselves.</summary>
public sealed class PayableDtoTests
{
    [Fact]
    public void The_dto_shows_the_payable_quantity_per_line_and_the_amount_including_vat()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);

        var dto = InvoicingMapper.ToDto(invoice, order);

        dto.Lines.ShouldHaveSingleItem().Payable.ShouldBe(2);
        dto.PayableInclVat.ShouldBe(57_960m);
        dto.PayableInclVatText.ShouldBe(Money.Zar(57_960m).ToDisplayString());
    }

    [Fact]
    public void Nothing_is_payable_on_hold_when_paid_or_while_the_order_is_unknown()
    {
        var order = Fixture.Order(received: 3);
        var held = Fixture.Matched(Fixture.Invoice(quantity: 3), order);
        held.Hold("check bank details");
        var paid = Fixture.Matched(Fixture.Invoice(quantity: 3, number: "INV-2", sequence: 2), order);
        paid.Pay(paid.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        var unknown = Fixture.Matched(Fixture.Invoice(quantity: 1, number: "INV-3", sequence: 3), order);

        foreach (var (invoice, view) in new[] { (held, order), (paid, order), (unknown, (OrderView?)null) })
        {
            var dto = InvoicingMapper.ToDto(invoice, view);
            dto.PayableInclVat.ShouldBe(0m);
            dto.Lines.ShouldAllBe(l => l.Payable == 0);
        }
    }

    [Fact]
    public void A_price_mismatched_line_a_closed_order_and_another_supplier_pay_nothing()
    {
        var order = Fixture.Order(received: 3);
        var priced = Fixture.Matched(Fixture.Invoice(quantity: 3, price: 30_000m, number: "INV-P"), order);
        var closed = Fixture.Order(received: 3);
        var invoiceOnClosed = Fixture.Matched(Fixture.Invoice(quantity: 3, number: "INV-C"), closed);
        closed.Cancel();
        var elsewhere = Fixture.Order(received: 3, supplier: "sup-2");
        var wrongSupplier = Fixture.Matched(Fixture.Invoice(quantity: 3, number: "INV-S"), Fixture.Order(received: 3));

        foreach (var (invoice, view) in new[] { (priced, order), (invoiceOnClosed, closed), (wrongSupplier, elsewhere) })
        {
            var dto = InvoicingMapper.ToDto(invoice, view);
            dto.PayableInclVat.ShouldBe(0m);
            dto.Lines.ShouldAllBe(l => l.Payable == 0);
        }
    }

    [Fact]
    public void A_line_whose_sku_is_not_the_order_lines_carries_no_order_quantity_or_price()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 1, sku: "OTHER-1", number: "INV-X"), order);

        var line = InvoicingMapper.ToDto(invoice, order).Lines.ShouldHaveSingleItem();

        line.Ordered.ShouldBeNull();
        line.OrderUnitPriceExclVat.ShouldBeNull();
        line.OrderUnitPriceText.ShouldBeNull();
        line.MatchReason.ShouldNotBeNull();
    }
}
