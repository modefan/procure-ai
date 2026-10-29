namespace ProcureAi.Contracts.Assistant;

/// <summary>
/// Integration event: a policy document's newest version moved to another status (<c>Uploaded</c>, <c>Checking</c>, <c>Embedding</c>,
/// <c>Ready</c>, <c>Flagged</c>, <c>Failed</c> or <c>Removed</c> when the document was removed), on the <c>assistant</c> exchange.
/// </summary>
public sealed record PolicyDocumentStatusChanged(
    Guid DocumentId,
    string TenantId,
    int Version,
    string Status,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
