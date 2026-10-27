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

        request.Status.ShouldBe(RequestStatus.Cancelled);
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
    public void Reject_only_from_submitted()
    {
        var draft = Draft();
        var approved = Submitted();
        approved.Approve(Now);
        var submitted = Submitted();

        Should.Throw<DomainException>(() => draft.Reject("No", Now));
        Should.Throw<DomainException>(() => approved.Reject("No", Now));
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
}
