using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Domain;
using Shouldly;

namespace ProcureAi.Purchasing.Tests;

public sealed class PurchaseOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static PurchaseOrder NewOrder(int quantity = 3) => PurchaseOrder.Create(
        "acme", Guid.NewGuid(), OrderNumber.FromSequence(1), "PR-0001", "alice", "CC-100", "sup-1",
        [new OrderLine(1, "LAP-14", "Laptop 14 inch", quantity, Money.Zar(25_200m))], Now);

    private static PurchaseOrder TwoLineOrder()
    {
        var order = PurchaseOrder.Create(
            "acme", Guid.NewGuid(), OrderNumber.FromSequence(2), "PR-0002", "alice", "CC-100", "sup-1",
            [new OrderLine(1, "A", "a", 5, Money.Zar(10m)), new OrderLine(2, "B", "b", 5, Money.Zar(20m))], Now);
        order.Confirm("SUP-2", null, Now);
        return order;
    }

    private static PurchaseOrder Confirmed(int quantity = 3)
    {
        var order = NewOrder(quantity);
        order.Confirm("SUP-1", null, Now).ShouldBeTrue();
        return order;
    }

    private static PurchaseOrder PartiallyReceived()
    {
        var order = Confirmed();
        order.Receive([(1, 2)], "bob", null, Now);
        return order;
    }

    [Fact]
    public void Create_works_out_totals_with_VAT_on_the_total()
    {
        var order = NewOrder();

        order.Status.ShouldBe(OrderStatus.PendingSupplier);
        order.Number.ShouldBe(OrderNumber.FromSequence(1));
        order.TotalExclVat.ToDisplayString().ShouldBe("R 75 600,00");
        order.Vat.ToDisplayString().ShouldBe("R 11 340,00");
        order.TotalInclVat.ToDisplayString().ShouldBe("R 86 940,00");
        order.DomainEvents.OfType<PurchaseOrderCreatedDomainEvent>().ShouldHaveSingleItem().Order.ShouldBeSameAs(order);
    }

    [Fact]
    public void Create_needs_a_line() =>
        Should.Throw<DomainException>(() => PurchaseOrder.Create(
            "acme", Guid.NewGuid(), OrderNumber.FromSequence(1), "PR-0001", "alice", "CC-100", "sup-1", [], Now));

    [Fact]
    public void A_line_needs_a_positive_quantity() =>
        Should.Throw<DomainException>(() => new OrderLine(1, "X", "x", 0, Money.Zar(1m)))
            .Message.ShouldContain("positive");

    [Fact]
    public void A_line_in_another_currency_is_refused() =>
        Should.Throw<DomainException>(() => new OrderLine(1, "X", "x", 1, new Money(1m, "USD")));

    [Fact]
    public void Receive_part_of_the_goods_leaves_the_rest_outstanding()
    {
        var order = Confirmed();

        var receipt = order.Receive([(1, 2)], "bob", " DN-77 ", Now);

        order.Status.ShouldBe(OrderStatus.PartiallyReceived);
        order.Lines[0].Received.ShouldBe(2);
        order.Lines[0].Outstanding.ShouldBe(1);
        receipt.DeliveryNote.ShouldBe("DN-77");
        receipt.Lines.ShouldHaveSingleItem().ShouldBe(new ReceiptLine(1, 2));
        order.Receipts.ShouldHaveSingleItem().ShouldBeSameAs(receipt);
    }

    [Fact]
    public void Receiving_the_rest_completes_the_order()
    {
        var order = PartiallyReceived();

        order.Receive([(1, 1)], "bob", null, Now);

        order.Status.ShouldBe(OrderStatus.Received);
        order.Lines[0].Outstanding.ShouldBe(0);
    }

    [Fact]
    public void Receiving_more_than_is_outstanding_is_refused_and_changes_nothing()
    {
        var order = PartiallyReceived();

        Should.Throw<DomainException>(() => order.Receive([(1, 2)], "bob", null, Now)).Message.ShouldContain("outstanding");

        order.Lines[0].Received.ShouldBe(2);
        order.Receipts.Count.ShouldBe(1);
    }

    [Fact]
    public void Receiving_before_the_supplier_confirmed_is_refused() =>
        Should.Throw<DomainException>(() => NewOrder().Receive([(1, 1)], "bob", null, Now));

    [Fact]
    public void The_same_line_twice_in_one_receipt_is_refused()
    {
        var order = TwoLineOrder();

        Should.Throw<DomainException>(() => order.Receive([(1, 1), (1, 1)], "bob", null, Now)).Message.ShouldContain("more than once");
        order.Lines[0].Received.ShouldBe(0);
    }

    [Fact]
    public void A_receipt_with_a_bad_quantity_or_unknown_line_is_refused()
    {
        var order = Confirmed();

        Should.Throw<DomainException>(() => order.Receive([(1, 0)], "bob", null, Now));
        Should.Throw<DomainException>(() => order.Receive([(9, 1)], "bob", null, Now));
        Should.Throw<DomainException>(() => order.Receive([], "bob", null, Now));
    }

    [Fact]
    public void A_failing_line_in_a_receipt_leaves_earlier_lines_untouched()
    {
        var order = TwoLineOrder();

        Should.Throw<DomainException>(() => order.Receive([(1, 1), (2, 99)], "bob", null, Now));

        order.Lines.ShouldAllBe(l => l.Received == 0);
        order.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public void Close_short_after_a_partial_delivery_closes_what_is_outstanding()
    {
        var order = PartiallyReceived();

        var closed = order.CloseShort("Supplier discontinued it", "carol", Now);

        closed.ShouldHaveSingleItem().ShouldBe((1, 1));
        order.Status.ShouldBe(OrderStatus.ClosedShort);
        order.Lines[0].ClosedShort.ShouldBe(1);
        order.Lines[0].Outstanding.ShouldBe(0);
    }

    [Fact]
    public void Close_short_with_nothing_received_points_to_cancel() =>
        Should.Throw<DomainException>(() => Confirmed().CloseShort("why", "carol", Now))
            .Message.ShouldBe("Nothing received yet: cancel the request instead.");

    [Fact]
    public void Close_short_needs_a_reason() =>
        Should.Throw<DomainException>(() => PartiallyReceived().CloseShort(" ", "carol", Now));

    [Fact]
    public void A_fully_received_order_cannot_be_closed_short()
    {
        var order = Confirmed();
        order.Receive([(1, 3)], "bob", null, Now);

        Should.Throw<DomainException>(() => order.CloseShort("why", "carol", Now));
    }

    [Fact]
    public void Cancel_before_the_supplier_has_it_needs_no_call_to_the_supplier()
    {
        var order = NewOrder();

        order.Cancel("changed my mind", Now).ShouldBe(new CancelOutcome.Cancelled(false));

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.CancelReason.ShouldBe("changed my mind");
        order.CancelledAt.ShouldBe(Now);
        order.SupplierCancelPending.ShouldBeFalse();
    }

    [Fact]
    public void Cancel_after_the_supplier_confirmed_must_tell_the_supplier()
    {
        var order = Confirmed();

        order.Cancel("changed my mind", Now).ShouldBe(new CancelOutcome.Cancelled(true));

        order.SupplierCancelPending.ShouldBeTrue();
    }

    [Fact]
    public void Cancel_after_sending_tells_the_supplier()
    {
        var order = NewOrder();
        order.MarkSent("SUP-1", Now);

        order.Cancel("x", Now).ShouldBe(new CancelOutcome.Cancelled(true));
    }

    [Fact]
    public void Cancel_after_goods_arrived_is_refused()
    {
        var order = PartiallyReceived();

        order.Cancel("x", Now).ShouldBe(new CancelOutcome.Refused("Goods have already been received"));

        order.Status.ShouldBe(OrderStatus.PartiallyReceived);
    }

    [Fact]
    public void Cancelling_twice_is_the_same_answer_again()
    {
        var order = NewOrder();
        order.Cancel("x", Now);

        order.Cancel("y", Now.AddHours(1)).ShouldBeOfType<CancelOutcome.AlreadyCancelled>();

        order.CancelReason.ShouldBe("x");
    }

    [Fact]
    public void Cancel_of_a_rejected_order_is_refused()
    {
        var order = NewOrder();
        order.RejectBySupplier("out of stock", Now).ShouldBeTrue();

        order.Cancel("x", Now).ShouldBe(new CancelOutcome.Refused("The supplier rejected the order"));
    }

    [Fact]
    public void Confirm_from_pending_works_because_the_webhook_can_beat_our_commit()
    {
        var order = NewOrder();

        order.Confirm("SUP-9", new DateOnly(2026, 11, 3), Now).ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.SupplierReference.ShouldBe("SUP-9");
        order.ExpectedDelivery.ShouldBe(new DateOnly(2026, 11, 3));
    }

    [Fact]
    public void Confirm_after_sending_works_and_twice_changes_nothing()
    {
        var order = NewOrder();
        order.MarkSent("SUP-9", Now);

        order.Confirm("SUP-9", null, Now).ShouldBeTrue();
        order.Confirm("SUP-9", null, Now).ShouldBeFalse();
    }

    [Fact]
    public void Confirm_after_cancel_is_ignored()
    {
        var order = NewOrder();
        order.Cancel("x", Now);

        order.Confirm("SUP-9", null, Now).ShouldBeFalse();

        order.Status.ShouldBe(OrderStatus.Cancelled);
    }

    [Fact]
    public void Confirm_after_rejection_is_refused()
    {
        var order = NewOrder();
        order.RejectBySupplier("no", Now);

        Should.Throw<DomainException>(() => order.Confirm("SUP-9", null, Now));
    }

    [Fact]
    public void Rejecting_follows_the_same_rules_as_confirming()
    {
        var pending = NewOrder();
        pending.RejectBySupplier("no", Now).ShouldBeTrue();
        pending.RejectBySupplier("no", Now).ShouldBeFalse();
        pending.Status.ShouldBe(OrderStatus.Rejected);

        var cancelled = NewOrder();
        cancelled.Cancel("x", Now);
        cancelled.RejectBySupplier("no", Now).ShouldBeFalse();

        Should.Throw<DomainException>(() => Confirmed().RejectBySupplier("no", Now));
    }

    [Fact]
    public void Mark_sent_is_idempotent_for_the_same_reference_and_refused_for_another()
    {
        var order = NewOrder();
        order.MarkSent("SUP-1", Now);
        order.MarkSent("SUP-1", Now);

        Should.Throw<DomainException>(() => order.MarkSent("SUP-2", Now));
        order.Status.ShouldBe(OrderStatus.SentToSupplier);
    }

    [Fact]
    public void A_supplier_failure_counts_the_attempt_and_plans_the_next()
    {
        var order = NewOrder();

        order.RecordSupplierFailure("timeout", Now.AddSeconds(5));
        order.RecordSupplierFailure("503", Now.AddSeconds(15));

        order.SupplierAttempts.ShouldBe(2);
        order.LastSupplierError.ShouldBe("503");
        order.NextSupplierAttemptAt.ShouldBe(Now.AddSeconds(15));
        order.Status.ShouldBe(OrderStatus.PendingSupplier);
    }

    [Fact]
    public void An_acceptance_that_lands_after_a_cancel_still_has_to_be_cancelled_at_the_supplier()
    {
        var order = NewOrder();
        order.Cancel("x", Now).ShouldBe(new CancelOutcome.Cancelled(false));

        order.RecordAcceptedAfterCancel("SUP-5");

        order.SupplierReference.ShouldBe("SUP-5");
        order.SupplierCancelPending.ShouldBeTrue();
        order.MarkSupplierCancelled();
        order.SupplierCancelPending.ShouldBeFalse();
    }

    [Fact]
    public void The_tombstone_has_no_number_and_no_lines()
    {
        var requestId = Guid.NewGuid();

        var tombstone = PurchaseOrder.CancelledBeforeCreation("acme", requestId, "request cancelled", Now);

        tombstone.Status.ShouldBe(OrderStatus.Cancelled);
        tombstone.Number.ShouldBeNull();
        tombstone.Lines.ShouldBeEmpty();
        tombstone.RequestId.ShouldBe(requestId);
        tombstone.CancelReason.ShouldBe("request cancelled");
        tombstone.Cancel("again", Now).ShouldBeOfType<CancelOutcome.AlreadyCancelled>();
        tombstone.Confirm("SUP-1", null, Now).ShouldBeFalse();
    }
}
