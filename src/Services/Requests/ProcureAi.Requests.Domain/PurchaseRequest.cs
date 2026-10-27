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

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<RequestLine> Lines => _lines.AsReadOnly();

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

    /// <summary>Rejected by an approver or by the system (the saga's RequestRejected). Only a submitted request can be rejected.</summary>
    public void Reject(string reason, DateTimeOffset now)
    {
        EnsureSubmitted("rejected");
        RequireText(reason, "A rejection reason");
        Status = RequestStatus.Rejected;
        RejectionReason = reason.Trim();
        RejectedAt = now;
    }

    /// <summary>
    /// Cancels a draft, a submitted or an approved request. Only a request the approval saga knows of (submitted or approved)
    /// raises <see cref="PurchaseRequestCancelled"/>, so the saga can stop and release the budget; a draft has nothing to undo.
    /// (Plan 03 adds: not after the goods were received.)
    /// </summary>
    public void Cancel(string cancelledBy, string? reason, DateTimeOffset now)
    {
        RequireText(cancelledBy, "Who cancels");
        if (Status is not (RequestStatus.Draft or RequestStatus.Submitted or RequestStatus.Approved))
        {
            throw new DomainException($"A {Status.ToString().ToLowerInvariant()} request cannot be cancelled.");
        }

        var known = Status != RequestStatus.Draft;
        Status = RequestStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = cancelledBy;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (known)
        {
            Raise(new PurchaseRequestCancelled(Id, TenantId, Number.ToString(), cancelledBy, CancellationReason, now));
        }
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
