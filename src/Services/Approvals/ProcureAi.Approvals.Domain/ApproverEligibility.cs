using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>The facts of a request that decide who may approve it: its total incl. VAT, who raised it and its cost center's organisation.</summary>
public sealed record RequestFacts(Money TotalInclVat, string RequesterId, OrganisationSnapshot Organisation);

/// <summary>One rule of approver eligibility, whether the candidate meets it and a sentence that says so (for the authorization check).</summary>
public sealed record EligibilityReason(string Code, bool Passed, string Text);

/// <summary>
/// The single place the approver eligibility rules live. <see cref="ApprovalRouting"/> treats a candidate as eligible at a level
/// when every reason passed, and the authorization check shows the same reasons, so the two cannot disagree:
/// <list type="bullet">
/// <item><c>permission</c>: their roles grant <c>requests.approve</c>.</item>
/// <item><c>cost-center</c> (manager level), <c>department</c> (department-head level) or <c>finance</c> (finance level): the
/// organisation of the request's cost center has them in that place.</item>
/// <item><c>limit</c>: their approval limit covers the total (a limit equal to the total is enough).</item>
/// <item><c>not-requester</c>: separation of duties, nobody approves their own request.</item>
/// </list>
/// </summary>
public static class ApproverEligibility
{
    public const string Permission = "permission";
    public const string CostCenter = "cost-center";
    public const string Department = "department";
    public const string Finance = "finance";
    public const string Limit = "limit";
    public const string NotRequester = "not-requester";

    public static IReadOnlyList<EligibilityReason> Check(ApprovalLevel level, Candidate candidate, RequestFacts request)
    {
        var name = candidate.Name;
        var org = request.Organisation;
        var total = request.TotalInclVat;
        return
        [
            new(Permission, candidate.CanApprove, candidate.CanApprove
                ? $"{name} has a role with {Permissions.RequestsApprove}"
                : $"{name} has no role with {Permissions.RequestsApprove}"),
            Responsibility(level, candidate, org),
            new(Limit, candidate.ApprovalLimit.Amount >= total.Amount, candidate.ApprovalLimit.Amount >= total.Amount
                ? $"{total.ToDisplayString()} is within the {candidate.ApprovalLimit.ToDisplayString()} limit"
                : $"{total.ToDisplayString()} is above the {candidate.ApprovalLimit.ToDisplayString()} limit"),
            new(NotRequester, candidate.UserId != request.RequesterId, candidate.UserId != request.RequesterId
                ? $"Separation of duties: {name} is not the requester"
                : $"Separation of duties: {name} is the requester"),
        ];
    }

    /// <summary>True when the candidate meets every rule at <paramref name="level"/>.</summary>
    public static bool IsEligible(ApprovalLevel level, Candidate candidate, RequestFacts request) =>
        Check(level, candidate, request).All(r => r.Passed);

    private static EligibilityReason Responsibility(ApprovalLevel level, Candidate candidate, OrganisationSnapshot org)
    {
        var name = candidate.Name;
        switch (level)
        {
            case ApprovalLevel.Manager:
                var manages = org.Manager?.UserId == candidate.UserId;
                return new(CostCenter, manages, manages
                    ? $"Cost center {org.CostCenterCode} is assigned to {name}"
                    : $"{name} is not the manager of cost center {org.CostCenterCode}");
            case ApprovalLevel.DepartmentHead:
                var heads = org.DepartmentHead?.UserId == candidate.UserId;
                var department = org.DepartmentCode is null ? "the department" : $"department {org.DepartmentCode}";
                return new(Department, heads, heads
                    ? $"{name} heads {department} (cost center {org.CostCenterCode})"
                    : $"{name} does not head {department} (cost center {org.CostCenterCode})");
            default:
                var finance = org.Finance.Any(f => f.UserId == candidate.UserId);
                return new(Finance, finance, finance ? $"{name} is a finance approver" : $"{name} is not a finance approver");
        }
    }
}
