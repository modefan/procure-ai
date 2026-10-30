namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: one step was approved.</summary>
public sealed record ApprovalStepApproved(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    string ApprovedBy,
    DateTimeOffset OccurredAt,
    string? Comment = null) : IIntegrationEvent;
