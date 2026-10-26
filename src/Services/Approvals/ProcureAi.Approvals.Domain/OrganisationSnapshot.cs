using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>A possible approver: their approval limit and whether their role grants the approve permission.</summary>
public sealed record Candidate(string UserId, Money ApprovalLimit, bool CanApprove);

/// <summary>Who can approve for one cost center, taken from the Access service at routing time.</summary>
public sealed record OrganisationSnapshot(
    string CostCenterCode,
    Candidate? Manager,
    Candidate? DepartmentHead,
    IReadOnlyList<Candidate> Finance);
