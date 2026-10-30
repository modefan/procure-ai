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
            var (landedLevel, approvers) = LandOn(level, totalInclVat, requesterId, org);
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

    /// <summary>
    /// Escalates a step that timed out: returns a REPLACEMENT for <paramref name="from"/>, not an extra step.
    /// <list type="bullet">
    /// <item><c>StepNo</c> equals <c>from.StepNo</c>.</item>
    /// <item><c>Level</c> is the next level above <c>from.Level</c> with eligible approvers.</item>
    /// <item><c>CoversLevels</c> is <c>from.CoversLevels</c> plus every level above <c>from.Level</c> up to and including the landed level (ascending, no duplicates).</item>
    /// <item><c>ApproverIds</c> are the eligible approvers at the landed level, excluding the requester and anyone in <paramref name="alreadyApproved"/>.</item>
    /// </list>
    /// Returns null from Finance, or when nobody above is eligible. The caller replaces <paramref name="from"/> with the result
    /// and merges it with the following step when that step has the same <c>Level</c>.
    /// </summary>
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
                var covers = from.CoversLevels
                    .Concat(Enumerable.Range((int)from.Level + 1, level - from.Level).Select(l => (ApprovalLevel)l))
                    .Distinct()
                    .Order()
                    .ToList();
                return new PlannedStep(from.StepNo, level, covers, approvers);
            }
        }

        return null;
    }

    private static (ApprovalLevel Level, IReadOnlyList<string> Approvers) LandOn(
        ApprovalLevel level, Money total, string requesterId, OrganisationSnapshot org)
    {
        for (var current = level; current <= ApprovalLevel.Finance; current++)
        {
            var approvers = ApproversAt(current, total, requesterId, org);
            if (approvers.Count > 0)
            {
                return (current, approvers);
            }
        }

        throw new NoEligibleApproverException(ApprovalLevel.Finance);
    }

    private static bool Merges(ApprovalLevel a, IReadOnlyList<string> aApprovers, ApprovalLevel b, IReadOnlyList<string> bApprovers) =>
        a == b || (aApprovers.Count == 1 && bApprovers.Count == 1 && aApprovers[0] == bApprovers[0]);

    // Who is eligible is decided by ApproverEligibility alone (the authorization check explains the same rules).
    private static IReadOnlyList<string> ApproversAt(ApprovalLevel level, Money total, string requesterId, OrganisationSnapshot org)
    {
        var facts = new RequestFacts(total, requesterId, org);
        return level switch
        {
            ApprovalLevel.Manager => Single(level, org.Manager, facts),
            ApprovalLevel.DepartmentHead => Single(level, org.DepartmentHead, facts),
            _ => org.Finance
                .Where(c => ApproverEligibility.IsEligible(level, c, facts))
                .OrderBy(c => c.ApprovalLimit.Amount)
                .ThenBy(c => c.UserId, StringComparer.Ordinal)
                .Select(c => c.UserId)
                .ToList(),
        };
    }

    private static IReadOnlyList<string> Single(ApprovalLevel level, Candidate? candidate, RequestFacts facts) =>
        candidate is not null && ApproverEligibility.IsEligible(level, candidate, facts) ? [candidate.UserId] : [];
}
