using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Requests.Domain;

public sealed class PurchaseRequest : Entity<Guid>
{
    private readonly List<RequestLine> _lines = [];
    private int _lastLineNo;

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

        if (unitPriceExclVat.Amount < 0)
        {
            throw new DomainException("Unit price cannot be negative.");
        }

        _lines.Add(new RequestLine(++_lastLineNo, sku, description, quantity, unitPriceExclVat));
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

    public void Submit(DateTimeOffset now)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainException("A request needs at least one line to be submitted.");
        }

        Status = RequestStatus.Submitted;
        SubmittedAt = now;
        Raise(new RequestSubmitted(Id, TenantId, Number.ToString(), RequesterId, CostCenterCode, TotalInclVat.Amount, now));
    }

    private void EnsureDraft()
    {
        if (Status != RequestStatus.Draft)
        {
            throw new DomainException("A submitted request can no longer be changed.");
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
