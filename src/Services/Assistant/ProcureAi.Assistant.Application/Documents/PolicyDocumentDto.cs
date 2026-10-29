using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Assistant.Application.Documents;

/// <summary>
/// A policy document as the admin page shows it: its newest version and where its ingestion stands.
/// </summary>
/// <param name="Version">The newest uploaded version (1, 2, ...).</param>
/// <param name="CurrentVersion">The version retrieval searches; 0 while none is ready (a replaced document keeps the old one until the new one is).</param>
/// <param name="Status"><c>Uploaded</c>, <c>Checking</c>, <c>Embedding</c>, <c>Ready</c>, <c>Flagged</c> or <c>Failed</c>.</param>
/// <param name="Progress">Embedding progress of the newest version, 0 to 100.</param>
/// <param name="ChunkCount">The searchable chunks of the current version.</param>
/// <param name="FlaggedReason">Why the document guard refused the newest version, when it did.</param>
/// <param name="FailureReason">Why the newest version could not be ingested, when it failed.</param>
/// <param name="RowVersion">The concurrency version: send it as <c>If-Match: "&lt;rowVersion&gt;"</c> to replace or remove (also the item's ETag).</param>
public sealed record PolicyDocumentDto(
    Guid Id,
    string Title,
    string FileName,
    int Version,
    int CurrentVersion,
    string Status,
    int Progress,
    int ChunkCount,
    DateTimeOffset UploadedAt,
    string UploadedByName,
    string? FlaggedReason,
    string? FailureReason,
    uint RowVersion);

public static class PolicyDocumentView
{
    /// <summary>The name shown for documents nobody uploaded (the demo seed).</summary>
    public const string SystemName = "System";

    public static PolicyDocumentDto ToDto(PolicyDocument document, string? uploadedByName) => new(
        document.Id, document.Title, document.FileName, document.Version, document.CurrentVersion, document.Status.ToString(),
        document.Progress, document.ChunkCount, document.UploadedAt,
        uploadedByName ?? document.UploadedBy, document.FlaggedReason, document.FailureReason, document.RowVersion);

    /// <summary>The DTOs of <paramref name="documents"/>, with one cached Access lookup per distinct uploader (a failed lookup shows the user ID).</summary>
    public static async Task<IReadOnlyList<PolicyDocumentDto>> ToDtosAsync(
        IReadOnlyList<PolicyDocument> documents, IAccessDirectory access, CancellationToken cancellationToken)
    {
        var names = new Dictionary<(string Tenant, string User), string?>();
        foreach (var key in documents.Select(d => (d.TenantId, d.UploadedBy)).Distinct())
        {
            names[key] = await NameOfAsync(access, key.UploadedBy, key.TenantId, cancellationToken);
        }

        return [.. documents.Select(d => ToDto(d, names[(d.TenantId, d.UploadedBy)]))];
    }

    public static async Task<PolicyDocumentDto> ToDtoAsync(PolicyDocument document, IAccessDirectory access, CancellationToken cancellationToken) =>
        (await ToDtosAsync([document], access, cancellationToken))[0];

    private static async Task<string?> NameOfAsync(IAccessDirectory access, string userId, string tenantId, CancellationToken cancellationToken)
    {
        if (userId == SystemCaller.Id)
        {
            return SystemName;
        }

        try
        {
            return await access.GetDisplayNameAsync(userId, tenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null; // the list must not fail because a name could not be looked up
        }
    }
}
