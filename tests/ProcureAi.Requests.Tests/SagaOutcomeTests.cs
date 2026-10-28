using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Domain;
using Shouldly;

namespace ProcureAi.Requests.Tests;

/// <summary>The request follows the approval saga: approved, rejected or cancelled.</summary>
public class SagaOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    private static PurchaseRequest Draft()
    {
        var request = PurchaseRequest.CreateDraft("acme", RequestNumber.From(7), "tom", "Laptops", "CC-100", "sup-1");
        request.AddLine("LAP-1", "Laptop", 3, Money.Zar(25200m));
        return request;
    }

    private static PurchaseRequest Submitted()
    {
        var request = Draft();
        request.Submit(Now.AddHours(-1));
        request.ClearDomainEvents();
        return request;
    }

    [Fact]
    public void Cancel_from_draft_raises_no_event()
    {
        var request = Draft();

        request.Cancel("tom", "Not needed", Now);

        request.Status.ShouldBe(RequestStatus.Cancelled);
        request.CancelledAt.ShouldBe(Now);
        request.CancelledBy.ShouldBe("tom");
        request.CancellationReason.ShouldBe("Not needed");
        // Nothing outside knows a draft: no saga, no reservation, nothing to release.
        request.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Cancel_from_submitted_raises_PurchaseRequestCancelled()
    {
        var request = Submitted();

        request.Cancel("tom", null, Now);

        request.Status.ShouldBe(RequestStatus.Cancelled);
        var cancelled = request.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PurchaseRequestCancelled>();
        cancelled.RequestId.ShouldBe(request.Id);
        cancelled.TenantId.ShouldBe("acme");
        cancelled.Number.ShouldBe("PR-0007");
        cancelled.CancelledBy.ShouldBe("tom");
        cancelled.Reason.ShouldBeNull();
        cancelled.CancelledAt.ShouldBe(Now);
    }

    [Fact]
    public void Cancel_after_approval_raises_PurchaseRequestCancelled()
    {
        var request = Submitted();
        request.Approve(Now.AddMinutes(-5));

        request.Cancel("tom", "Supplier gone", Now);

        // An approved request may already have an order: the saga confirms or refuses before it is cancelled.
        request.Status.ShouldBe(RequestStatus.Cancelling);
        request.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PurchaseRequestCancelled>().Reason.ShouldBe("Supplier gone");
    }

    [Fact]
    public void Cancel_of_rejected_or_cancelled_request_is_refused()
    {
        var rejected = Submitted();
        rejected.Reject("Over budget", Now);
        var cancelled = Draft();
        cancelled.Cancel("tom", null, Now);

        Should.Throw<DomainException>(() => rejected.Cancel("tom", null, Now));
        Should.Throw<DomainException>(() => cancelled.Cancel("tom", null, Now));
    }

    [Fact]
    public void Cancel_reason_is_trimmed_and_blank_means_none()
    {
        var request = Draft();

        request.Cancel("tom", "   ", Now);

        request.CancellationReason.ShouldBeNull();
    }

    [Fact]
    public void Reject_only_from_submitted_approved_or_ordered()
    {
        var draft = Draft();
        var approved = Submitted();
        approved.Approve(Now);
        var submitted = Submitted();

        Should.Throw<DomainException>(() => draft.Reject("No", Now));
        approved.Reject("Supplier said no", Now);
        approved.Status.ShouldBe(RequestStatus.Rejected);
        submitted.Reject("Over budget", Now);

        submitted.Status.ShouldBe(RequestStatus.Rejected);
        submitted.RejectionReason.ShouldBe("Over budget");
        submitted.RejectedAt.ShouldBe(Now);
        Should.Throw<DomainException>(() => submitted.Reject("Again", Now));
    }

    [Fact]
    public void Reject_needs_a_reason() =>
        Should.Throw<DomainException>(() => Submitted().Reject(" ", Now));

    [Fact]
    public void Approve_only_from_submitted()
    {
        var submitted = Submitted();

        submitted.Approve(Now);

        submitted.Status.ShouldBe(RequestStatus.Approved);
        submitted.ApprovedAt.ShouldBe(Now);
        submitted.DomainEvents.ShouldBeEmpty();
        Should.Throw<DomainException>(() => Draft().Approve(Now));
        Should.Throw<DomainException>(() => submitted.Approve(Now));
    }

    [Fact]
    public void Approved_or_rejected_request_can_no_longer_be_changed()
    {
        var approved = Submitted();
        approved.Approve(Now);

        Should.Throw<DomainException>(() => approved.UpdateTitle("x"));
        Should.Throw<DomainException>(() => approved.Submit(Now));
    }

    private static PurchaseRequest Ordered()
    {
        var request = Submitted();
        request.Approve(Now.AddMinutes(-5));
        request.MarkOrdered(OrderId, "PO-0001", Now.AddMinutes(-4));
        return request;
    }

    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Cancel_ordered_request_is_cancelling_and_raises_event()
    {
        var request = Ordered();

        request.Cancel("tom", "Changed our mind", Now);

        request.Status.ShouldBe(RequestStatus.Cancelling);
        request.StatusBeforeCancel.ShouldBe(RequestStatus.Ordered);
        request.CancelledBy.ShouldBe("tom");
        request.CancellationReason.ShouldBe("Changed our mind");
        request.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PurchaseRequestCancelled>();
        Should.Throw<DomainException>(() => request.Cancel("tom", null, Now));
    }

    [Fact]
    public void Confirmed_cancellation_cancels_and_a_repeat_is_a_no_op()
    {
        var request = Ordered();
        request.Cancel("tom", null, Now);

        request.ConfirmCancellation(Now.AddMinutes(1));
        request.ConfirmCancellation(Now.AddMinutes(2));

        request.Status.ShouldBe(RequestStatus.Cancelled);
        request.CancelledAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Refused_cancellation_restores_previous_status()
    {
        var approved = Submitted();
        approved.Approve(Now);
        approved.Cancel("tom", "x", Now);
        var ordered = Ordered();
        ordered.Cancel("tom", "x", Now);

        approved.RefuseCancellation("Goods were received", Now);
        ordered.RefuseCancellation("Goods were received", Now);

        approved.Status.ShouldBe(RequestStatus.Approved);
        ordered.Status.ShouldBe(RequestStatus.Ordered);
        ordered.CancellationRefusedReason.ShouldBe("Goods were received");
        // The refused cancel is over: nothing of it is left to show.
        ordered.CancellationReason.ShouldBeNull();
        ordered.CancelledAt.ShouldBeNull();
        ordered.StatusBeforeCancel.ShouldBeNull();
    }

    [Fact]
    public void Refusal_of_an_approved_cancel_whose_order_is_known_restores_Ordered()
    {
        var request = Submitted();
        request.Approve(Now);
        request.Cancel("tom", null, Now);
        request.MarkOrdered(OrderId, "PO-0001", Now);
        request.Status.ShouldBe(RequestStatus.Cancelling);

        request.RefuseCancellation("Goods were received", Now);

        request.Status.ShouldBe(RequestStatus.Ordered);
    }

    [Fact]
    public void Refusal_can_follow_a_confirmation_that_was_handled_first()
    {
        var request = Ordered();
        request.Cancel("tom", null, Now);
        request.ConfirmCancellation(Now);

        request.RefuseCancellation("Goods were received", Now);

        request.Status.ShouldBe(RequestStatus.Ordered);
    }

    [Fact]
    public void Complete_from_ordered_records_the_spend()
    {
        var request = Ordered();

        request.Complete(Money.Zar(86940m), Now);

        request.Status.ShouldBe(RequestStatus.Completed);
        request.SpentInclVat.ShouldBe(Money.Zar(86940m));
        request.CompletedAt.ShouldBe(Now);
        request.OrderNumber.ShouldBe("PO-0001");
    }

    [Fact]
    public void Complete_only_from_ordered_approved_or_cancelling()
    {
        var draft = Draft();
        var submitted = Submitted();
        var rejected = Submitted();
        rejected.Reject("No", Now);
        var cancelled = Draft();
        cancelled.Cancel("tom", null, Now);
        var completed = Ordered();
        completed.Complete(Money.Zar(1m), Now);

        foreach (var request in new[] { draft, submitted, rejected, cancelled, completed })
        {
            Should.Throw<DomainException>(() => request.Complete(Money.Zar(1m), Now));
        }
    }

    [Fact]
    public void Complete_while_cancelling_clears_the_pending_cancel()
    {
        var request = Ordered();
        request.Cancel("tom", "x", Now);

        request.Complete(Money.Zar(100m), Now);

        request.Status.ShouldBe(RequestStatus.Completed);
        request.StatusBeforeCancel.ShouldBeNull();
        request.CancellationReason.ShouldBeNull();
        request.CancelledAt.ShouldBeNull();
        request.CancelledBy.ShouldBeNull();
    }

    [Fact]
    public void Complete_from_approved_leaves_the_order_for_RequestOrdered_to_fill()
    {
        var request = Submitted();
        request.Approve(Now);

        request.Complete(Money.Zar(100m), Now);
        request.OrderNumber.ShouldBeNull();
        request.MarkOrdered(OrderId, "PO-0001", Now);

        request.Status.ShouldBe(RequestStatus.Completed);
        request.OrderId.ShouldBe(OrderId);
        request.OrderNumber.ShouldBe("PO-0001");
    }

    [Fact]
    public void MarkOrdered_stores_null_for_an_empty_number_and_never_overwrites_a_known_one()
    {
        var request = Submitted();
        request.Approve(Now);

        request.MarkOrdered(OrderId, "", Now);
        request.Status.ShouldBe(RequestStatus.Ordered);
        request.OrderNumber.ShouldBeNull();

        request.MarkOrdered(OrderId, "PO-0001", Now);
        request.OrderNumber.ShouldBe("PO-0001");
        request.MarkOrdered(OrderId, " ", Now);
        request.OrderNumber.ShouldBe("PO-0001");
    }

    [Fact]
    public void Supplier_rejection_from_ordered()
    {
        var request = Ordered();

        request.Reject("The supplier cannot deliver", Now);

        request.Status.ShouldBe(RequestStatus.Rejected);
        request.RejectionReason.ShouldBe("The supplier cannot deliver");
    }

    [Fact]
    public void Cancel_of_a_completed_request_is_refused()
    {
        var request = Ordered();
        request.Complete(Money.Zar(1m), Now);

        Should.Throw<DomainException>(() => request.Cancel("tom", null, Now)).Message.ShouldContain("completed");
    }
}
