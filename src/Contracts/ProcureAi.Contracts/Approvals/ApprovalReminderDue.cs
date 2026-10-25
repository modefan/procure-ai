namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: a step is still open and its approvers should be reminded.</summary>
public sealed record ApprovalReminderDue(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    IReadOnlyList<string> ApproverIds,
    int ReminderNo,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
