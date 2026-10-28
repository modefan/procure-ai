namespace ProcureAi.Contracts.Approvals;

/// <summary>
/// Integration event: a step timed out and moved to the next level (<c>Manager</c>, <c>DepartmentHead</c> or <c>Finance</c>). <c>Number</c>
/// (the request number, for the bell and email) was added in plan 04 and is null in messages published before it.
/// </summary>
public sealed record ApprovalEscalated(
    Guid RequestId,
    string TenantId,
    Guid StepId,
    string FromLevel,
    string ToLevel,
    IReadOnlyList<string> ApproverIds,
    DateTimeOffset OccurredAt,
    string? Number = null) : IIntegrationEvent;
