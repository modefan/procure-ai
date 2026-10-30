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
/// completed one can be indexed as a "purchase" chunk (ruling 9). A read copy of another service's data: the Requests database is the
/// truth. The events can arrive in any order, so a row can exist before its submit data (<see cref="Placeholder"/>,
/// <see cref="HasSubmitData"/>); each event writes only its own columns, and the row version guards against concurrent writers.
/// </summary>
public sealed class Purchase
{
    public const int NumberMaxLength = 50;
    public const int TitleMaxLength = 200;
    public const int CodeMaxLength = 100;

    /// <summary>The separator between the parts of a purchase chunk's text.</summary>
    public const string PartSeparator = " · ";

    private const char IdSeparator = ',';

    private Purchase()
    {
    }

    public Purchase(
        Guid requestId, string tenantId, string number, string title, string costCenterCode, Money totalInclVat, string? supplierCode,
        string linesSummary, DateTimeOffset submittedAt)
        : this(requestId, tenantId, number, submittedAt)
    {
        RecordSubmitted(number, title, costCenterCode, totalInclVat, supplierCode, linesSummary, submittedAt);
    }

    private Purchase(Guid requestId, string tenantId, string number, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A purchase needs a tenant.");
        }

        RequestId = requestId;
        TenantId = tenantId;
        Number = number;
        CreatedAt = createdAt;
        Status = PurchaseStatus.Submitted;
    }

    /// <summary>A row for a request whose submit data has not arrived yet (an approval or completion came first).</summary>
    public static Purchase Placeholder(Guid requestId, string tenantId, string number, DateTimeOffset createdAt) =>
        new(requestId, tenantId, number, createdAt);

    public Guid RequestId { get; private set; }

    /// <summary>When the row was created: the submit time, or the arrival of the first event for a placeholder (which expires, see Assistant:Purchases:PlaceholderMaxAge).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

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

    /// <summary>When the request was submitted; null until <c>RequestSubmitted</c> arrived.</summary>
    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The row version (Postgres <c>xmin</c>): two events of the same request handled at once cannot overwrite each other.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Whether <c>RequestSubmitted</c> arrived (title, cost center, total and lines are known).</summary>
    public bool HasSubmitData => SubmittedAt is not null;

    /// <summary>Completed and with its submit data: it can be written as a purchase chunk.</summary>
    public bool IsIndexable => Status == PurchaseStatus.Completed && HasSubmitData;

    /// <summary>The approvers' user IDs, in the order of the event.</summary>
    public IReadOnlyList<string> ApproverIdList =>
        ApproverIds.Split(IdSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The submit data (<c>RequestSubmitted</c>). The status is kept when a later event already moved it on.</summary>
    public void RecordSubmitted(
        string number, string title, string costCenterCode, Money totalInclVat, string? supplierCode, string linesSummary, DateTimeOffset submittedAt)
    {
        Number = Limit(number, NumberMaxLength);
        Title = Limit(title, TitleMaxLength);
        CostCenterCode = Limit(costCenterCode, CodeMaxLength);
        TotalInclVat = totalInclVat;
        SupplierCode = string.IsNullOrWhiteSpace(supplierCode) ? null : Limit(supplierCode, CodeMaxLength);
        LinesSummary = linesSummary;
        SubmittedAt = submittedAt;
    }

    /// <summary>The final approval (<c>RequestApproved</c>) with the approvers' user IDs.</summary>
    public void RecordApproved(IEnumerable<string> approverIds, DateTimeOffset approvedAt)
    {
        ApproverIds = string.Join(IdSeparator, approverIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal));
        ApprovedAt = approvedAt;
        if (Status == PurchaseStatus.Submitted)
        {
            Status = PurchaseStatus.Approved;
        }
    }

    /// <summary>The request is completed (<c>RequestCompleted</c>): from now on it is indexed.</summary>
    public void RecordCompleted(DateTimeOffset completedAt)
    {
        CompletedAt = completedAt;
        Status = PurchaseStatus.Completed;
    }

    /// <summary>
    /// The text of the purchase chunk (ruling 9): "PR-0982 · Laptops for IT · IT-200 · R 140 000,00 incl. VAT · approved by Maria Naidoo,
    /// Sipho Dlamini · completed 2026-09-14 · lines: 2 x Laptop". The approval part is left out when no approver is known.
    /// </summary>
    /// <param name="approverNames">The approvers' display names, in order.</param>
    /// <param name="completedOn">The local (South African) date of completion.</param>
    public string ToChunkText(IReadOnlyList<string> approverNames, DateOnly completedOn)
    {
        if (!IsIndexable)
        {
            throw new DomainException("Only a completed purchase with its submit data is indexed.");
        }

        var parts = new List<string> { Number, Title, CostCenterCode, $"{TotalInclVat.ToDisplayString()} incl. VAT" };
        if (approverNames.Count > 0)
        {
            parts.Add("approved by " + string.Join(", ", approverNames));
        }

        parts.Add("completed " + completedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        parts.Add("lines: " + LinesSummary);
        return string.Join(PartSeparator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string Limit(string? text, int max)
    {
        var value = (text ?? "").Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
