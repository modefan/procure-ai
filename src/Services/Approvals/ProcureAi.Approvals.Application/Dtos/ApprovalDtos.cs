namespace ProcureAi.Approvals.Application.Dtos;

/// <summary>
/// One step of a request's approval chain as the caller sees it. <c>DueAt</c> is when the open step escalates (null for a step
/// that has not opened yet). <c>CanAct</c> says whether the caller may approve or reject it now; when not,
/// <c>CannotActReason</c> says why (e.g. "You raised this request"). <c>Version</c> is the ETag to send in <c>If-Match</c>.
/// <c>RequesterName</c> is the requester's display name (filled in the inbox; null when Access does not know them or in a chain),
/// <c>OpenedAt</c> when the step became the open one (null while not open, or for a step opened before this was recorded).
/// </summary>
public sealed record ApprovalStepDto(
    Guid Id,
    Guid RequestId,
    string RequestNumber,
    string RequestTitle,
    string RequesterId,
    decimal TotalInclVat,
    string TotalInclVatDisplay,
    int StepNo,
    string Level,
    string Status,
    DateTimeOffset? DueAt,
    bool CanAct,
    string? CannotActReason,
    uint Version,
    string? RequesterName = null,
    DateTimeOffset? OpenedAt = null);

/// <summary>The approval chain of one request: the saga's state and every step, in order (an escalated step before its replacement).</summary>
public sealed record ApprovalChainDto(Guid RequestId, string State, IReadOnlyList<ApprovalStepDto> Steps);

/// <summary>Who would approve a request of this amount on this cost center, or why nobody can.</summary>
public sealed record ApprovalPreviewDto(bool Possible, string? Reason, IReadOnlyList<PreviewStepDto> Steps);

public sealed record PreviewStepDto(int StepNo, string Level, IReadOnlyList<string> ApproverNames);
