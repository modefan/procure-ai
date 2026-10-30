namespace ProcureAi.Approvals.Application.AuthorizationCheck;

/// <summary>
/// Why a user may or may not <c>Action</c> (<c>view</c> or <c>approve</c>) a request: every rule in the order the APIs apply them,
/// each passed or failed with a sentence. <c>Allowed</c> is true when every step passed.
/// </summary>
public sealed record AuthorizationCheckDto(
    bool Allowed, string Action, string UserName, string RequestNumber, IReadOnlyList<CheckStepDto> Steps);

/// <summary>
/// One rule of the check: its code (<c>permission</c>, <c>tenant</c>, <c>open-step</c>, <c>cost-center</c>, <c>department</c>,
/// <c>finance</c>, <c>limit</c>, <c>not-requester</c>, <c>current-approver</c>), whether it passed, and why.
/// </summary>
public sealed record CheckStepDto(string Code, bool Passed, string Text);
