namespace ProcureAi.Approvals.Application.AuthorizationCheck;

/// <summary>
/// Why a user may or may not <c>Action</c> (<c>view</c> or <c>approve</c>) a request: every rule in the order the APIs apply them,
/// each passed or failed with a sentence. <c>Allowed</c> is true when every <em>gating</em> step passed: the ones the API enforces
/// (<c>permission</c>, <c>tenant</c>, <c>open-step</c>, <c>not-requester</c>, <c>current-approver</c>). The routing-eligibility
/// reasons (<c>cost-center</c> / <c>department</c> / <c>finance</c>, <c>limit</c>) are recomputed from Access's data today and only
/// explain whether routing would pick the user now; the approve action does not re-check them, so they never decide the outcome.
/// </summary>
public sealed record AuthorizationCheckDto(
    bool Allowed, string Action, string UserName, string RequestNumber, IReadOnlyList<CheckStepDto> Steps);

/// <summary>
/// One rule of the check: its code (<c>permission</c>, <c>tenant</c>, <c>open-step</c>, <c>cost-center</c>, <c>department</c>,
/// <c>finance</c>, <c>limit</c>, <c>not-requester</c>, <c>current-approver</c>), whether it passed, why, and whether it decides
/// <c>Allowed</c> (<c>Gating</c>: true for the rules the API enforces, false for the routing-eligibility explanation).
/// </summary>
public sealed record CheckStepDto(string Code, bool Passed, string Text, bool Gating);
