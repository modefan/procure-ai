namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event (saga): the cancel of an ordered request was refused.</summary>
public sealed record RequestCancellationRefused(
    Guid RequestId,
    string TenantId,
    string Number,
    string Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
