using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Domain;

/// <summary>Where a past purchase stands, from Requests' events (ruling 9). Only completed ones are indexed.</summary>
public enum PurchaseStatus
{
    Submitted,
    Approved,
    Completed,
    Rejected,
    Cancelled,
}

/// <summary>
/// The Assistant's copy of a purchase request (from <c>RequestSubmitted</c>, <c>RequestApproved</c>, <c>RequestCompleted</c>), so a
/// completed one can be indexed as a "purchase" chunk. A read copy of another service's data: the Requests database is the truth.
/// </summary>
public sealed class Purchase
{
    public const int NumberMaxLength = 50;
    public const int TitleMaxLength = 200;
    public const int CodeMaxLength = 100;

    private Purchase()
    {
    }

    public Purchase(
        Guid requestId, string tenantId, string number, string title, string costCenterCode, Money totalInclVat, string? supplierCode,
        string linesSummary, DateTimeOffset submittedAt)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A purchase needs a tenant.");
        }

        RequestId = requestId;
        TenantId = tenantId;
        Number = number;
        Title = title;
        CostCenterCode = costCenterCode;
        TotalInclVat = totalInclVat;
        SupplierCode = supplierCode;
        LinesSummary = linesSummary;
        SubmittedAt = submittedAt;
        Status = PurchaseStatus.Submitted;
    }

    public Guid RequestId { get; private set; }

    public string TenantId { get; private set; } = "";

    /// <summary>The request's human-readable number, e.g. PR-0982.</summary>
    public string Number { get; private set; } = "";

    public string Title { get; private set; } = "";

    public string CostCenterCode { get; private set; } = "";

    public Money TotalInclVat { get; private set; }

    public string? SupplierCode { get; private set; }

    /// <summary>The lines as one line of text ("2 x Laptop 14 inch, 1 x Docking station").</summary>
    public string LinesSummary { get; private set; } = "";

    /// <summary>The user IDs of the approvers, comma separated (names are looked up when the chunk is written).</summary>
    public string ApproverIds { get; private set; } = "";

    public PurchaseStatus Status { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }
}
