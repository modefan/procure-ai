using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>One approval step: <see cref="Level"/> is where it landed, <see cref="CoversLevels"/> the required levels it stands for.</summary>
public sealed record PlannedStep(
    int StepNo,
    ApprovalLevel Level,
    IReadOnlyList<ApprovalLevel> CoversLevels,
    IReadOnlyList<string> ApproverIds);

public sealed record ApprovalChain(IReadOnlyList<PlannedStep> Steps);

/// <summary>Nobody at or above <see cref="Level"/> may approve this request (limits, permissions or separation of duties).</summary>
public sealed class NoEligibleApproverException(ApprovalLevel level)
    : DomainException($"No eligible approver at level {level} or above.")
{
    public ApprovalLevel Level { get; } = level;
}
