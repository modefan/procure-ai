namespace ProcureAi.Contracts.Assistant;

/// <summary>
/// Integration event: the embedding of a policy document's version reached another 10 % step (published at most once per step, through
/// the outbox, on the <c>assistant</c> exchange). <paramref name="Percent"/> is 10, 20, ... 100.
/// </summary>
public sealed record PolicyDocumentProgressed(
    Guid DocumentId,
    string TenantId,
    int Version,
    int Percent,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
