namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: an approval step opened. <c>Level</c> is <c>Manager</c>, <c>DepartmentHead</c> or <c>Finance</c>; <c>DueAt</c> is when it escalates.</summary>
public sealed record ApprovalRequested(
    Guid RequestId,
    string TenantId,
    string Number,
    Guid StepId,
    int StepNo,
    string Level,
    IReadOnlyList<string> ApproverIds,
    DateTimeOffset DueAt,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
