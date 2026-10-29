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

    /// <summary>Whether <paramref name="version"/> is still the one to ingest: the newest upload of a document that was not removed.</summary>
    public bool IsNewest(int version) => !Removed && version == Version;

    /// <summary>
    /// A new file for the document: the next version, waiting for ingestion. The current version stays searchable until the new one
    /// is ready (<see cref="MarkReady"/> switches it).
    /// </summary>
    public void ReplaceFile(string fileName, string uploadedBy, DateTimeOffset at)
    {
        EnsureNotRemoved();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > FileNameMaxLength)
        {
            throw new DomainException($"A policy document needs a file name of 1 to {FileNameMaxLength} characters.");
        }

        FileName = fileName;
        UploadedBy = uploadedBy;
        UploadedAt = at;
        Version++;
        MoveTo(PolicyDocumentStatus.Uploaded, at);
        Progress = 0;
    }

    /// <summary>Ingestion of <paramref name="version"/> started: the document guard reads it. False when the version is stale.</summary>
    public bool StartChecking(int version, DateTimeOffset at) => Move(version, PolicyDocumentStatus.Checking, at);

    /// <summary>The version passed the guard and its chunks are being embedded. False when the version is stale.</summary>
    public bool StartEmbedding(int version, DateTimeOffset at) => Move(version, PolicyDocumentStatus.Embedding, at);

    /// <summary>Records the embedding progress (0 to 100) of the newest version. False when the version is stale.</summary>
    public bool SetProgress(int version, int percent, DateTimeOffset at)
    {
        if (!IsNewest(version))
        {
            return false;
        }

        Progress = Math.Clamp(percent, 0, 100);
        UpdatedAt = at;
        return true;
    }

    /// <summary>Every chunk of <paramref name="version"/> is embedded: it becomes the version retrieval searches. False when stale.</summary>
    public bool MarkReady(int version, int chunkCount, DateTimeOffset at)
    {
        if (!Move(version, PolicyDocumentStatus.Ready, at))
        {
            return false;
        }

        CurrentVersion = version;
        ChunkCount = chunkCount;
        Progress = 100;
        return true;
    }

    /// <summary>The document guard flagged the version (prompt injection): nothing of it is indexed. False when stale.</summary>
    public bool Flag(int version, string reason, DateTimeOffset at)
    {
        if (!Move(version, PolicyDocumentStatus.Flagged, at))
        {
            return false;
        }

        FlaggedReason = Shorten(reason);
        return true;
    }

    /// <summary>The version could not be ingested (unreadable file, model unavailable after the retries). False when stale.</summary>
    public bool Fail(int version, string reason, DateTimeOffset at)
    {
        if (!Move(version, PolicyDocumentStatus.Failed, at))
        {
            return false;
        }

        FailureReason = Shorten(reason);
        return true;
    }

    /// <summary>Soft delete: the document and its chunks disappear from lists and retrieval; the rows stay.</summary>
    public void Remove(DateTimeOffset at)
    {
        EnsureNotRemoved();
        Removed = true;
        UpdatedAt = at;
    }

    private bool Move(int version, PolicyDocumentStatus status, DateTimeOffset at)
    {
        if (!IsNewest(version))
        {
            return false;
        }

        MoveTo(status, at);
        return true;
    }

    private void MoveTo(PolicyDocumentStatus status, DateTimeOffset at)
    {
        Status = status;
        UpdatedAt = at;
        if (status is not PolicyDocumentStatus.Flagged)
        {
            FlaggedReason = null;
        }

        if (status is not PolicyDocumentStatus.Failed)
        {
            FailureReason = null;
        }
    }

    private void EnsureNotRemoved()
    {
        if (Removed)
        {
            throw new DomainException("The policy document was removed.");
        }
    }

    private static string Shorten(string reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "No reason given." : reason.Trim();
        return text.Length <= ReasonMaxLength ? text : text[..(ReasonMaxLength - 1)] + "…";
    }
}
