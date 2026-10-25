namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event: a step timed out and moved to the next level (<c>Manager</c>, <c>DepartmentHead</c> or <c>Finance</c>).</summary>
public sealed record ApprovalEscalated(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    string FromLevel,
    string ToLevel,
    IReadOnlyList<string> ApproverIds,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
