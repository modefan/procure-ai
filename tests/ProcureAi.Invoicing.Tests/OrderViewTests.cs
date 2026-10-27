using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

public sealed class OrderViewTests
{
    private static OrderView Settled(int paid, int closed, int received = 2)
    {
        var order = Fixture.Order(received);
        if (paid > 0)
        {
            var invoice = Fixture.Matched(Fixture.Invoice(quantity: received), order);
            invoice.Pay(invoice.PlanPayment(order, [(1, paid)]), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        }

        if (closed > 0)
        {
            order.ApplyClosedShort([(1, closed)]);
        }

        return order;
    }

    [Fact]
    public void Paid_two_and_closed_short_one_of_three_is_settled()
    {
        Settled(paid: 2, closed: 1).IsSettled.ShouldBeTrue();
    }

    [Fact]
    public void Paid_two_of_three_is_not_settled()
    {
        Settled(paid: 2, closed: 0).IsSettled.ShouldBeFalse();
    }

    [Fact]
    public void Nothing_paid_is_not_settled()
    {
        Fixture.Order(received: 3).IsSettled.ShouldBeFalse();
    }

    [Fact]
    public void A_cancelled_order_is_never_settled()
    {
        var order = Settled(paid: 2, closed: 1);

        order.Cancel();

        order.Status.ShouldBe(OrderViewStatus.Cancelled);
        order.IsSettled.ShouldBeFalse();
        order.MarkSettled(Fixture.Now).ShouldBeFalse();
    }

    [Fact]
    public void MarkSettled_is_true_only_once()
    {
        var order = Settled(paid: 2, closed: 1);

        order.MarkSettled(Fixture.Now).ShouldBeTrue();
        order.SettledAt.ShouldBe(Fixture.Now);
        order.MarkSettled(Fixture.Now.AddMinutes(1)).ShouldBeFalse();
        order.SettledAt.ShouldBe(Fixture.Now);
    }

    [Fact]
    public void MarkSettled_is_false_while_not_settled()
    {
        Settled(paid: 2, closed: 0).MarkSettled(Fixture.Now).ShouldBeFalse();
    }

    [Fact]
    public void Receipts_and_closing_short_cannot_exceed_the_order()
    {
        var order = Fixture.Order(received: 2);

        Should.Throw<DomainException>(() => order.ApplyReceipt([(1, 2)]));
        Should.Throw<DomainException>(() => order.ApplyClosedShort([(1, 2)]));
        Should.Throw<DomainException>(() => order.ApplyReceipt([(5, 1)])).Message.ShouldBe("The order has no line 5.");
        order.Lines[0].Received.ShouldBe(2);
    }

    [Fact]
    public void Every_change_moves_the_revision_so_the_version_serialises_the_order()
    {
        var order = Fixture.Order();
        var revision = order.Revision;

        order.RecordMatch();
        order.Revision.ShouldBe(revision + 1);
        order.ApplyReceipt([(1, 1)]);
        order.Revision.ShouldBe(revision + 2);
    }
}
