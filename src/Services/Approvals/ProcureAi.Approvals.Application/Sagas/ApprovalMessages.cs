namespace ProcureAi.Approvals.Application.Sagas;

/// <summary>The current user approves a step (sent by the API). <c>ExpectedVersion</c> is the step's version the user saw (If-Match).</summary>
public sealed record ApproveStep(Guid RequestId, Guid StepId, uint ExpectedVersion);

/// <summary>The current user rejects a step, and with it the request (sent by the API).</summary>
public sealed record RejectStep(Guid RequestId, Guid StepId, uint ExpectedVersion, string Reason);

public enum StepTimeoutKind
{
    /// <summary>The step has been open for <c>Timeouts:ReminderAfter</c>: remind its approvers.</summary>
    Reminder,

    /// <summary>The step has been open for <c>Timeouts:EscalateAfter</c>: move it up a level.</summary>
    Escalate,

    /// <summary>A step nobody is above stays overdue: remind again every <c>Timeouts:FinanceReminderEvery</c>.</summary>
    FinanceReminder,
}

/// <summary>
/// A durably scheduled timeout of one step. It is ignored unless that step is still the open one and <c>Attempt</c> is the one
/// expected (1 for the reminder and the escalation; the next link of the finance-reminder chain for a finance reminder), so a
/// late or repeated timeout changes nothing.
/// </summary>
public sealed record StepTimeout(Guid RequestId, Guid StepId, StepTimeoutKind Kind, int Attempt);
