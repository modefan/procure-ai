namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event (saga): the cancel of an ordered request went through.</summary>
public sealed record RequestCancellationConfirmed(
    Guid RequestId,
    string TenantId,
    string Number,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
