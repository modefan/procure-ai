namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event: a step is still open and its approvers should be reminded. <c>Number</c> (the request number, for the bell and
/// email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record ApprovalReminderDue(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    IReadOnlyList<string> ApproverIds,
    int ReminderNo,
    DateTimeOffset OccurredAt,
    string? Number = null) : IIntegrationEvent;
