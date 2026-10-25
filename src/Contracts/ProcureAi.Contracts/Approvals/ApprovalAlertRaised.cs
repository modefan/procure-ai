namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: a step timed out at the last level or has no approver; someone must look at it.</summary>
public sealed record ApprovalAlertRaised(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    string Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
