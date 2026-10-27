using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Application.Commands;
using ProcureAi.Invoicing.Application.Mapping;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

public sealed class MatchDecisionsTests
{
    private static readonly Money Tolerance = Fixture.Tolerance;

    [Fact]
    public void A_goods_receipt_turns_a_mismatched_invoice_into_a_match()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);
        invoice.Status.ShouldBe(InvoiceStatus.Mismatched);
        order.ApplyReceipt([(1, 1)]);

        var outcomes = MatchDecisions.Rematch(order, [invoice], Tolerance);

        var outcome = outcomes.ShouldHaveSingleItem();
        outcome.Changed.ShouldBeTrue();
        outcome.WasAwaitingOrder.ShouldBeFalse();
        invoice.Status.ShouldBe(InvoiceStatus.Matched);
        InvoicingMapper.ToOutcomeEvent(invoice, Fixture.Now).ShouldBeOfType<ProcureAi.Contracts.Invoicing.InvoiceMatched>();
    }

    [Fact]
    public void An_unchanged_outcome_is_not_news()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);

        MatchDecisions.Rematch(order, [invoice], Tolerance).ShouldHaveSingleItem().Changed.ShouldBeFalse();
    }

    [Fact]
    public void Earlier_invoices_are_matched_first_and_claim_the_goods()
    {
        var order = Fixture.Order(received: 3);
        var first = Fixture.Invoice(quantity: 2, sequence: 1, number: "INV-1");
        var second = Fixture.Invoice(quantity: 2, sequence: 2, number: "INV-2");

        // Passed in reverse order on purpose: the sequence decides.
        var outcomes = MatchDecisions.Rematch(order, [second, first], Tolerance);

        outcomes.Select(o => o.Invoice).ShouldBe([first, second]);
        first.Status.ShouldBe(InvoiceStatus.Matched);
        second.Status.ShouldBe(InvoiceStatus.Mismatched);
        second.Match!.Reasons.ShouldBe(["Line 1: invoiced 2, but only 1 received and not yet invoiced"]);
    }

    [Fact]
    public void Invoices_waiting_for_the_order_take_it_over_and_are_matched()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Invoice(supplier: "", sku: "");
        invoice.Status.ShouldBe(InvoiceStatus.AwaitingOrder);

        var outcome = MatchDecisions.Rematch(order, [invoice], Tolerance).ShouldHaveSingleItem();

        outcome.WasAwaitingOrder.ShouldBeTrue();
        outcome.Changed.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Matched);
        invoice.RequestId.ShouldBe(order.RequestId);
        invoice.SupplierId.ShouldBe("sup-1");
        invoice.Lines[0].Sku.ShouldBe("LAP-14");
    }

    [Fact]
    public void Held_paid_and_other_orders_invoices_are_left_alone()
    {
        var order = Fixture.Order(received: 3);
        var held = Fixture.Matched(Fixture.Invoice(number: "INV-1", sequence: 1), order);
        held.Hold("check bank details");
        var paid = Fixture.Matched(Fixture.Invoice(quantity: 1, number: "INV-2", sequence: 2), order);
        paid.Pay(paid.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        var elsewhere = Domain.Invoice.Create(
            "acme", Guid.NewGuid(), Guid.Empty, "sup-1", "INV-3", new DateOnly(2026, 10, 27), 3, InvoiceSource.Manual,
            [new InvoiceLine(1, "LAP-14", "Laptop", 1, Money.Zar(25_200m))], Money.Zar(28_980m), "{}", Fixture.Now);

        MatchDecisions.Rematch(order, [held, paid, elsewhere], Tolerance).ShouldBeEmpty();
        elsewhere.Status.ShouldBe(InvoiceStatus.AwaitingOrder);
    }

    [Fact]
    public void Held_and_paid_earlier_invoices_still_count_for_later_ones()
    {
        var order = Fixture.Order(received: 3);
        var earlier = Fixture.Matched(Fixture.Invoice(quantity: 2, number: "INV-1", sequence: 1), order);
        earlier.Hold("check");
        var later = Fixture.Invoice(quantity: 2, number: "INV-2", sequence: 2);

        MatchDecisions.Rematch(order, [earlier, later], Tolerance);

        later.Status.ShouldBe(InvoiceStatus.Mismatched);
    }

    [Fact]
    public void Matching_only_one_invoice_leaves_the_others()
    {
        var order = Fixture.Order(received: 3);
        var first = Fixture.Invoice(quantity: 1, number: "INV-1", sequence: 1);
        var second = Fixture.Invoice(quantity: 1, number: "INV-2", sequence: 2);

        var outcome = MatchDecisions.Rematch(order, [first, second], Tolerance, only: second).ShouldHaveSingleItem();

        outcome.Invoice.ShouldBeSameAs(second);
        first.Status.ShouldBe(InvoiceStatus.AwaitingOrder);
    }

    [Fact]
    public void Every_match_writes_the_order_view_and_no_match_leaves_it_alone()
    {
        var order = Fixture.Order(received: 3);
        var revision = order.Revision;

        MatchDecisions.Rematch(order, [], Tolerance);
        order.Revision.ShouldBe(revision);

        MatchDecisions.Rematch(order, [Fixture.Invoice()], Tolerance);
        order.Revision.ShouldBe(revision + 1);
    }

    [Fact]
    public void A_replayed_invoice_is_recognised_by_content_not_by_number_alone()
    {
        var invoice = Fixture.Invoice();
        var same = Fixture.Invoice().Lines;

        invoice.HasSameContentAs(Fixture.OrderId, new DateOnly(2026, 10, 27), same, Money.Zar(86_940m)).ShouldBeTrue();
        invoice.HasSameContentAs(Fixture.OrderId, new DateOnly(2026, 10, 28), same, Money.Zar(86_940m)).ShouldBeFalse();
        invoice.HasSameContentAs(Fixture.OrderId, new DateOnly(2026, 10, 27), same, Money.Zar(86_941m)).ShouldBeFalse();
        invoice.HasSameContentAs(Guid.NewGuid(), new DateOnly(2026, 10, 27), same, Money.Zar(86_940m)).ShouldBeFalse();
        invoice.HasSameContentAs(Fixture.OrderId, new DateOnly(2026, 10, 27), Fixture.Invoice(quantity: 2).Lines, Money.Zar(86_940m)).ShouldBeFalse();
    }

    [Fact]
    public void Hold_rules()
    {
        var invoice = Fixture.Invoice();

        Should.Throw<DomainException>(() => invoice.Hold(" ")).Message.ShouldBe("Reason is required.");
        Should.Throw<DomainException>(() => invoice.ReleaseHold()).Message.ShouldBe("The invoice is not on hold.");
        invoice.Hold("bank details");
        invoice.OnHold.ShouldBeTrue();
        invoice.HoldReason.ShouldBe("bank details");
        Should.Throw<DomainException>(() => invoice.Hold("again")).Message.ShouldBe("The invoice is already on hold.");
        invoice.ReleaseHold();
        invoice.HoldReason.ShouldBeNull();
    }
}
