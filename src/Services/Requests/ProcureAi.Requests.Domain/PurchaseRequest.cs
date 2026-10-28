using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Requests.Domain;

public sealed class PurchaseRequest : Entity<Guid>
{
    private readonly List<RequestLine> _lines = [];

    private PurchaseRequest()
    {
    }

    public string TenantId { get; private set; } = "";

    public RequestNumber Number { get; private set; }

    public string RequesterId { get; private set; } = "";

    public string Title { get; private set; } = "";

    public string CostCenterCode { get; private set; } = "";

    public string SupplierId { get; private set; } = "";

    public RequestStatus Status { get; private set; } = RequestStatus.Draft;

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    /// <summary>Why the approval saga rejected it (an approver's reason, or the system's, e.g. the budget was refused).</summary>
    public string? RejectionReason { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>The user the cancellation was for (the requester).</summary>
    public string? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>The purchase order created for the request (from the saga's RequestOrdered).</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>The order's number; null until known (the saga announces an order settled before its creation without one).</summary>
    public string? OrderNumber { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>What was paid in the end, incl. VAT.</summary>
    public Money? SpentInclVat { get; private set; }

    /// <summary>While <see cref="RequestStatus.Cancelling"/>: where the request was, to go back to when the cancel is refused.</summary>
    public RequestStatus? StatusBeforeCancel { get; private set; }

    /// <summary>Why the last cancel was refused (e.g. the goods were received); cleared by the next cancel.</summary>
    public string? CancellationRefusedReason { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<RequestLine> Lines => _lines.AsReadOnly();

    // What each saga outcome applies to. The handler asks these instead of keeping its own lists, so the two cannot drift.

    /// <summary>Only a submitted request can be approved; a late approval of an ordered or completed request changes nothing.</summary>
    public bool CanApprove => Status == RequestStatus.Submitted;

    /// <summary>Submitted too: the order can be announced before the approval is handled (the messages are not ordered).</summary>
    public bool CanMarkOrdered =>
        Status is RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Ordered or RequestStatus.Completed or RequestStatus.Cancelling;

    /// <summary>Submitted and approved: the completion can overtake the approval and the order. Cancelling: the settlement overtook the cancel.</summary>
    public bool CanComplete =>
        Status is RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Ordered or RequestStatus.Cancelling;

    /// <summary>Submitted, or approved/ordered when the supplier rejects the order, or cancelling when that races with a cancel.</summary>
    public bool CanReject =>
        Status is RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Ordered or RequestStatus.Cancelling;

    public bool CanConfirmCancellation => Status is RequestStatus.Cancelling or RequestStatus.Cancelled;

    public bool CanRefuseCancellation => Status is RequestStatus.Cancelling or RequestStatus.Cancelled;

    public Money TotalExclVat => _lines.Aggregate(Money.Zero, (sum, line) => sum + line.TotalExclVat);

    /// <summary>VAT on the whole request (not per line), rounded to cents, midpoints away from zero (see <see cref="Money.Vat"/>).</summary>
    public Money Vat => TotalExclVat.Vat();

    public Money TotalInclVat => TotalExclVat + Vat;

    public static PurchaseRequest CreateDraft(
        string tenantId, RequestNumber number, string requesterId, string title, string costCenterCode, string supplierId)
    {
        RequireText(tenantId, "Tenant");
        RequireText(requesterId, "Requester");
        RequireText(costCenterCode, "Cost center");
        RequireText(supplierId, "Supplier");
        RequireText(title, "Title");
        if (number.Sequence <= 0)
        {
            throw new DomainException("A request number is required.");
        }

        return new PurchaseRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Number = number,
            RequesterId = requesterId,
            Title = title.Trim(),
            CostCenterCode = costCenterCode,
            SupplierId = supplierId,
        };
    }

    public void UpdateTitle(string title)
    {
        EnsureDraft();
        RequireText(title, "Title");
        Title = title.Trim();
    }

    public void AddLine(string sku, string description, int quantity, Money unitPriceExclVat)
    {
        EnsureDraft();
        RequireText(sku, "SKU");
        RequireText(description, "Description");
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be positive.");
        }

        if (unitPriceExclVat.Currency != Money.ZarCurrency)
        {
            throw new DomainException($"Only {Money.ZarCurrency} is supported, got {unitPriceExclVat.Currency}.");
        }

        if (unitPriceExclVat.Amount < 0)
        {
            throw new DomainException("Unit price cannot be negative.");
        }

        _lines.Add(new RequestLine(NextLineNo(), sku, description, quantity, unitPriceExclVat));
    }

    public void RemoveLine(int lineNo)
    {
        EnsureDraft();
        var index = _lines.FindIndex(l => l.LineNo == lineNo);
        if (index < 0)
        {
            throw new DomainException($"Line {lineNo} does not exist.");
        }

        _lines.RemoveAt(index);
    }

    /// <summary>Throws when the request cannot be submitted now (not a draft, or no lines), before anything is asked of other services.</summary>
    public void EnsureCanSubmit()
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainException("A request needs at least one line to be submitted.");
        }
    }

    public void Submit(DateTimeOffset now)
    {
        EnsureCanSubmit();
        Status = RequestStatus.Submitted;
        SubmittedAt = now;
        Raise(new PurchaseRequestSubmitted(Id, TenantId, Number.ToString(), Title, RequesterId, CostCenterCode, TotalInclVat.Amount, now, SupplierId, _lines.ToList()));
    }

    /// <summary>Every approval step approved (the saga's RequestApproved). Only a submitted request can be approved.</summary>
    public void Approve(DateTimeOffset now)
    {
        EnsureSubmitted("approved");
        Status = RequestStatus.Approved;
        ApprovedAt = now;
    }

    /// <summary>
    /// Rejected by an approver or by the system (the saga's RequestRejected). A submitted request can be rejected; so can an approved
    /// or ordered one, when the supplier rejects the order; a cancelling one too (the pending cancel is dropped, the saga ended).
    /// </summary>
    public void Reject(string reason, DateTimeOffset now)
    {
        if (!CanReject)
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request cannot be rejected.");
        }

        RequireText(reason, "A rejection reason");
        ClearPendingCancel();
        Status = RequestStatus.Rejected;
        RejectionReason = reason.Trim();
        RejectedAt = now;
    }

    /// <summary>
    /// Cancels a draft, a submitted, an approved or an ordered request. A draft is cancelled silently (nothing outside knows it); a
    /// submitted one is cancelled at once and <see cref="PurchaseRequestCancelled"/> lets the saga stop and release the budget; an
    /// approved or ordered one becomes <see cref="RequestStatus.Cancelling"/> until the saga confirms (the order was cancelled) or
    /// refuses (the goods were received) it. Anything else cannot be cancelled.
    /// </summary>
    public void Cancel(string cancelledBy, string? reason, DateTimeOffset now)
    {
        RequireText(cancelledBy, "Who cancels");
        if (Status is not (RequestStatus.Draft or RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Ordered))
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request cannot be cancelled.");
        }

        var known = Status != RequestStatus.Draft;
        var pending = Status is RequestStatus.Approved or RequestStatus.Ordered;
        StatusBeforeCancel = pending ? Status : null;
        Status = pending ? RequestStatus.Cancelling : RequestStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = cancelledBy;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CancellationRefusedReason = null;
        if (known)
        {
            Raise(new PurchaseRequestCancelled(Id, TenantId, Number.ToString(), cancelledBy, CancellationReason, now));
        }
    }

    /// <summary>
    /// The purchase order exists (the saga's RequestOrdered). An approved (or, when the approval is still on its way, submitted) request becomes ordered. In any other status that has an
    /// order (ordered, completed, cancelling) only the order's details are filled in, which is how a late or repeated message is
    /// harmless. An empty number (the saga had none) is stored as null and never replaces a known one.
    /// </summary>
    public void MarkOrdered(Guid orderId, string? orderNumber, DateTimeOffset now)
    {
        if (!CanMarkOrdered)
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request cannot be ordered.");
        }

        OrderId = orderId;
        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            OrderNumber = orderNumber.Trim();
        }

        if (Status is RequestStatus.Submitted or RequestStatus.Approved)
        {
            Status = RequestStatus.Ordered;
        }
    }

    /// <summary>
    /// Fully paid (the saga's RequestCompleted). Normally from ordered; also from cancelling (the settlement overtook the cancel, so
    /// the pending cancel is cleared) and from approved or submitted (RequestCompleted overtook RequestOrdered or RequestApproved; RequestOrdered fills in the order later).
    /// </summary>
    public void Complete(Money spent, DateTimeOffset now)
    {
        if (!CanComplete)
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request cannot be completed.");
        }

        Status = RequestStatus.Completed;
        SpentInclVat = spent;
        CompletedAt = now;
        ClearPendingCancel();
    }

    /// <summary>The saga cancelled the order and released the budget (RequestCancellationConfirmed). A repeat changes nothing.</summary>
    public void ConfirmCancellation(DateTimeOffset now)
    {
        if (Status == RequestStatus.Cancelled)
        {
            return;
        }

        if (!CanConfirmCancellation)
        {
            throw new DomainException($"Only a request being cancelled can be confirmed cancelled; this one is {Status.ToString().ToLowerInvariant()}.");
        }

        Status = RequestStatus.Cancelled;
        StatusBeforeCancel = null;
        CancelledAt ??= now;
    }

    /// <summary>
    /// The cancel was refused (RequestCancellationRefused, e.g. the goods were received): back to where the request was, with the
    /// reason. Also accepted after a confirmation that was handled first (the messages can arrive in either order). A request with
    /// a known order goes back to ordered even if it was only approved when the cancel was asked.
    /// </summary>
    public void RefuseCancellation(string reason, DateTimeOffset now)
    {
        if (!CanRefuseCancellation)
        {
            throw new DomainException($"Only a request being cancelled can have its cancel refused; this one is {Status.ToString().ToLowerInvariant()}.");
        }

        var before = StatusBeforeCancel ?? RequestStatus.Ordered;
        Status = before == RequestStatus.Approved && OrderId is not null ? RequestStatus.Ordered : before;
        CancellationRefusedReason = string.IsNullOrWhiteSpace(reason) ? "The cancellation was refused." : reason.Trim();
        ClearPendingCancel();
    }

    private void ClearPendingCancel()
    {
        StatusBeforeCancel = null;
        CancelledAt = null;
        CancelledBy = null;
        CancellationReason = null;
    }

    // Max + 1 (not a counter) so it keeps working after the lines are reloaded from the database.
    private int NextLineNo() => _lines.Count == 0 ? 1 : _lines.Max(l => l.LineNo) + 1;

    private void EnsureDraft()
    {
        if (Status != RequestStatus.Draft)
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request can no longer be changed.");
        }
    }

    private void EnsureSubmitted(string outcome)
    {
        if (Status != RequestStatus.Submitted)
        {
            throw new DomainException($"Only a submitted request can be {outcome}; this one is {Status.ToString().ToLowerInvariant()}.");
        }
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }
    }
}
