using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

public enum ApprovalStepStatus
{
    Pending,
    Approved,
    Rejected,

    /// <summary>Timed out and replaced by a step at a higher level (same step number, new ID).</summary>
    Escalated,

    /// <summary>No longer needed: the request was rejected or cancelled, or an escalated step took over its levels.</summary>
    Cancelled,
}

/// <summary>
/// One step of a request's approval chain, with a copy of the request facts the inbox shows. <see cref="Level"/> is where the
/// step landed, <see cref="CoversLevels"/> the required levels it stands for. Only a <see cref="ApprovalStepStatus.Pending"/>
/// step changes; every other status is final.
/// </summary>
public sealed class ApprovalStep
{
    private string[] _coversLevels = [];
    private string[] _approverIds = [];

    private ApprovalStep()
    {
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = "";

    public Guid RequestId { get; private set; }

    public string RequestNumber { get; private set; } = "";

    public string RequestTitle { get; private set; } = "";

    public string RequesterId { get; private set; } = "";

    public Money TotalInclVat { get; private set; }

    public int StepNo { get; private set; }

    public ApprovalLevel Level { get; private set; }

    public IReadOnlyList<ApprovalLevel> CoversLevels => [.. _coversLevels.Select(Enum.Parse<ApprovalLevel>)];

    /// <summary>Who may approve: one person for a manager or department-head step, every eligible finance approver for finance.</summary>
    public IReadOnlyList<string> ApproverIds => _approverIds;

    public ApprovalStepStatus Status { get; private set; }

    public string? ActedBy { get; private set; }

    public DateTimeOffset? ActedAt { get; private set; }

    /// <summary>When the step escalates; set when it becomes the current step.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    /// <summary>When the step became the open one (the approver started waiting); null for a step opened before this was recorded.</summary>
    public DateTimeOffset? OpenedAt { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public bool IsPending => Status == ApprovalStepStatus.Pending;

    /// <summary>The step is the open one of its chain: pending and opened (it has a due date). Only an open step can be acted on.</summary>
    public bool IsOpen => IsPending && DueAt is not null;

    public static ApprovalStep Plan(
        string tenantId, Guid requestId, string requestNumber, string requestTitle, string requesterId, Money totalInclVat,
        PlannedStep planned)
    {
        if (planned.ApproverIds.Contains(requesterId, StringComparer.Ordinal))
        {
            // Separation of duties: routing never plans this, so reaching it is a bug.
            throw new DomainException("The requester cannot approve their own request.");
        }

        return new ApprovalStep
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RequestId = requestId,
            RequestNumber = requestNumber,
            RequestTitle = requestTitle,
            RequesterId = requesterId,
            TotalInclVat = totalInclVat,
            StepNo = planned.StepNo,
            Level = planned.Level,
            _coversLevels = [.. planned.CoversLevels.Distinct().Order().Select(l => l.ToString())],
            _approverIds = [.. planned.ApproverIds],
            Status = ApprovalStepStatus.Pending,
        };
    }

    /// <summary>The step as the routing engine sees it (for escalation).</summary>
    public PlannedStep ToPlanned() => new(StepNo, Level, CoversLevels, ApproverIds);

    /// <summary>True when <paramref name="userId"/> is one of the approvers and did not raise the request.</summary>
    public bool MayBeActedOnBy(string userId) =>
        userId != RequesterId && _approverIds.Contains(userId, StringComparer.Ordinal);

    /// <summary>The step becomes the current one at <paramref name="openedAt"/>: it escalates at <paramref name="dueAt"/>.</summary>
    public void Open(DateTimeOffset dueAt, DateTimeOffset openedAt)
    {
        EnsurePending();
        DueAt = dueAt;
        OpenedAt = openedAt;
    }

    public void Approve(string userId, DateTimeOffset at) => Act(ApprovalStepStatus.Approved, userId, at);

    public void Reject(string userId, DateTimeOffset at) => Act(ApprovalStepStatus.Rejected, userId, at);

    public void Escalate()
    {
        EnsurePending();
        Status = ApprovalStepStatus.Escalated;
    }

    public void Cancel()
    {
        EnsurePending();
        Status = ApprovalStepStatus.Cancelled;
    }

    private void Act(ApprovalStepStatus status, string userId, DateTimeOffset at)
    {
        EnsurePending();
        if (!MayBeActedOnBy(userId))
        {
            throw new DomainException($"User {userId} may not act on approval step {StepNo}.");
        }

        Status = status;
        ActedBy = userId;
        ActedAt = at;
    }

    private void EnsurePending()
    {
        if (!IsPending)
        {
            throw new DomainException($"Approval step {StepNo} is {Status}, not pending.");
        }
    }
}
