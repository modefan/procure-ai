using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>
/// Decides who must approve a request. Thresholds are strictly "above"; nobody approves their own request;
/// a level with no eligible approver moves up to the next level; adjacent steps landing on the same approvers merge.
/// </summary>
public static class ApprovalRouting
{
    public static ApprovalChain Resolve(Money totalInclVat, string requesterId, OrganisationSnapshot org, ApprovalThresholds t)
    {
        var required = new List<ApprovalLevel> { ApprovalLevel.Manager };
        if (totalInclVat.Amount > t.DepartmentHeadAbove.Amount)
        {
            required.Add(ApprovalLevel.DepartmentHead);
        }

        if (totalInclVat.Amount > t.FinanceAbove.Amount)
        {
            required.Add(ApprovalLevel.Finance);
        }

        var landed = new List<(ApprovalLevel Level, List<ApprovalLevel> Covers, IReadOnlyList<string> Approvers)>();
        foreach (var level in required)
        {
            var (landedLevel, approvers) = LandOn(level, totalInclVat, requesterId, org, []);
            var previous = landed.Count > 0 ? landed[^1] : default;
            if (landed.Count > 0 && Merges(previous.Level, previous.Approvers, landedLevel, approvers))
            {
                previous.Covers.Add(level);
                landed[^1] = (landedLevel, previous.Covers, approvers);
            }
            else
            {
                landed.Add((landedLevel, [level], approvers));
            }
        }

        return new ApprovalChain(landed
            .Select((s, i) => new PlannedStep(i + 1, s.Level, s.Covers, s.Approvers))
            .ToList());
    }

    /// <summary>The next step above <paramref name="from"/> that has eligible approvers (not the requester, not anyone who already approved), or null.</summary>
    public static PlannedStep? Escalate(
        PlannedStep from,
        Money totalInclVat,
        string requesterId,
        OrganisationSnapshot org,
        IReadOnlyCollection<string> alreadyApproved)
    {
        for (var level = from.Level + 1; level <= ApprovalLevel.Finance; level++)
        {
            var approvers = ApproversAt(level, totalInclVat, requesterId, org)
                .Where(id => !alreadyApproved.Contains(id))
                .ToList();
            if (approvers.Count > 0)
            {
                return new PlannedStep(from.StepNo + 1, level, [level], approvers);
            }
        }

        return null;
    }

    private static (ApprovalLevel Level, IReadOnlyList<string> Approvers) LandOn(
        ApprovalLevel level, Money total, string requesterId, OrganisationSnapshot org, IReadOnlyCollection<string> exclude)
    {
        for (var current = level; current <= ApprovalLevel.Finance; current++)
        {
            var approvers = ApproversAt(current, total, requesterId, org).Where(id => !exclude.Contains(id)).ToList();
            if (approvers.Count > 0)
            {
                return (current, approvers);
            }
        }

        throw new NoEligibleApproverException(ApprovalLevel.Finance);
    }

    private static bool Merges(ApprovalLevel a, IReadOnlyList<string> aApprovers, ApprovalLevel b, IReadOnlyList<string> bApprovers) =>
        a == b || (aApprovers.Count == 1 && bApprovers.Count == 1 && aApprovers[0] == bApprovers[0]);

    private static IReadOnlyList<string> ApproversAt(ApprovalLevel level, Money total, string requesterId, OrganisationSnapshot org) =>
        level switch
        {
            ApprovalLevel.Manager => Single(org.Manager, total, requesterId),
            ApprovalLevel.DepartmentHead => Single(org.DepartmentHead, total, requesterId),
            _ => org.Finance
                .Where(c => Eligible(c, total, requesterId))
                .OrderBy(c => c.ApprovalLimit.Amount)
                .ThenBy(c => c.UserId, StringComparer.Ordinal)
                .Select(c => c.UserId)
                .ToList(),
        };

    private static IReadOnlyList<string> Single(Candidate? candidate, Money total, string requesterId) =>
        candidate is not null && Eligible(candidate, total, requesterId) ? [candidate.UserId] : [];

    private static bool Eligible(Candidate c, Money total, string requesterId) =>
        c.CanApprove && c.ApprovalLimit.Amount >= total.Amount && c.UserId != requesterId;
}
