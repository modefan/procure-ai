using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Domain;

/// <summary>Where a policy document's newest version stands. Ingestion (plan 05 Task 2b) moves it on.</summary>
public enum PolicyDocumentStatus
{
    Uploaded,
    Checking,
    Embedding,
    Ready,
    Flagged,
    Failed,
}

/// <summary>
/// A procurement policy document of one tenant (PDF or Markdown), in versions: <see cref="Version"/> is the newest upload,
/// <see cref="CurrentVersion"/> the one retrieval searches (0 until the first version is fully embedded). A replaced document keeps
/// its old version searchable until the new one is ready. Removing is a soft delete.
/// </summary>
public sealed class PolicyDocument : Entity<Guid>
{
    public const int TitleMaxLength = 200;
    public const int FileNameMaxLength = 255;
    public const int ReasonMaxLength = 500;

    private PolicyDocument()
    {
    }

    public PolicyDocument(Guid id, string tenantId, string title, string fileName, string uploadedBy, DateTimeOffset uploadedAt)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A policy document needs a tenant.");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > TitleMaxLength)
        {
            throw new DomainException($"A policy document needs a title of 1 to {TitleMaxLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > FileNameMaxLength)
        {
            throw new DomainException($"A policy document needs a file name of 1 to {FileNameMaxLength} characters.");
        }

        Id = id;
        TenantId = tenantId;
        Title = title.Trim();
        FileName = fileName;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
        UpdatedAt = uploadedAt;
        Version = 1;
        Status = PolicyDocumentStatus.Uploaded;
    }

    public string TenantId { get; private set; } = "";

    public string Title { get; private set; } = "";

    public string FileName { get; private set; } = "";

    /// <summary>The newest uploaded version (1, 2, ...).</summary>
    public int Version { get; private set; }

    /// <summary>The version retrieval searches; 0 while no version is fully embedded.</summary>
    public int CurrentVersion { get; private set; }

    public PolicyDocumentStatus Status { get; private set; }

    /// <summary>Embedding progress of the newest version, 0 to 100.</summary>
    public int Progress { get; private set; }

    public int ChunkCount { get; private set; }

    public string? FlaggedReason { get; private set; }

    public string? FailureReason { get; private set; }

    public bool Removed { get; private set; }

    public string UploadedBy { get; private set; } = "";

    public DateTimeOffset UploadedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The row version (Postgres <c>xmin</c>), the optimistic concurrency token and the ETag of the document.</summary>
    public uint RowVersion { get; private set; }
}
