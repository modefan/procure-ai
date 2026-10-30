using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>
/// A possible approver: their approval limit and whether their role grants the approve permission. <c>DisplayName</c> is only for
/// the texts of <see cref="ApproverEligibility"/> (the user ID when null); routing never looks at it.
/// </summary>
public sealed record Candidate(string UserId, Money ApprovalLimit, bool CanApprove, string? DisplayName = null)
{
    /// <summary>The name the eligibility texts use.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserId : DisplayName;
}

/// <summary>
/// Who can approve for one cost center, taken from the Access service at routing time. <c>DepartmentCode</c> is the cost
/// center's department (for the texts of <see cref="ApproverEligibility"/>; null when not known).
/// </summary>
public sealed record OrganisationSnapshot(
    string CostCenterCode,
    Candidate? Manager,
    Candidate? DepartmentHead,
    IReadOnlyList<Candidate> Finance,
    string? DepartmentCode = null);
