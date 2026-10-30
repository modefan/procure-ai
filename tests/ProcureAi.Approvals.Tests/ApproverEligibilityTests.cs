using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Approvals.Tests;

/// <summary>
/// <see cref="ApproverEligibility"/> is where routing's eligibility predicates live: the authorization check explains them and
/// <see cref="ApprovalRouting"/> decides with them, so the two can never disagree.
/// </summary>
public class ApproverEligibilityTests
{
    private static readonly ApprovalThresholds Thresholds = new(Money.Zar(20_000m), Money.Zar(200_000m));

    private static Candidate Maria(bool canApprove = true) => new("maria", Money.Zar(200_000m), canApprove, "Maria Naidoo");
    private static Candidate Dept() => new("dept", Money.Zar(500_000m), true);
    private static Candidate Thandi() => new("thandi", Money.Zar(2_000_000m), true);

    private static OrganisationSnapshot Org(Candidate? manager = null, Candidate? dept = null, params Candidate[] finance) =>
        new("CC-1", manager ?? Maria(), dept ?? Dept(), finance.Length == 0 ? [Thandi()] : finance, "IT");

    private static readonly Candidate[] ManyFinance =
    [
        new("zed", Money.Zar(3_000_000m), true),
        new("bob", Money.Zar(2_000_000m), true),
        new("amy", Money.Zar(2_000_000m), true),
        new("low", Money.Zar(100_000m), true),
        new("off", Money.Zar(9_000_000m), false),
        new("tom", Money.Zar(9_000_000m), true),
    ];

    /// <summary>The routing test cases (totals around the thresholds, every requester, every organisation shape of ApprovalRoutingTests).</summary>
    public static TheoryData<string, string, int> RoutingCases()
    {
        var data = new TheoryData<string, string, int>();
        string[] totals = ["5000", "20000.00", "20000.01", "30000", "200000.00", "200000.01", "250000", "600000", "5000000"];
        string[] requesters = ["tom", "maria", "dept", "thandi", "amy"];
        foreach (var total in totals)
        {
            foreach (var requester in requesters)
            {
                for (var org = 0; org < Orgs.Length; org++)
                {
                    data.Add(total, requester, org);
                }
            }
        }

        return data;
    }

    private static readonly OrganisationSnapshot[] Orgs =
    [
        Org(),
        Org(manager: Maria(canApprove: false)),
        new("CC-1", null, Dept(), [Thandi()]),
        Org(finance: ManyFinance),
        new("CC-1", Maria(), null, [Thandi()]),
        new("CC-1", Dept(), Dept(), [Thandi()]),
    ];

    private static IEnumerable<Candidate> SlotOf(ApprovalLevel level, OrganisationSnapshot org) => level switch
    {
        ApprovalLevel.Manager => org.Manager is null ? [] : [org.Manager],
        ApprovalLevel.DepartmentHead => org.DepartmentHead is null ? [] : [org.DepartmentHead],
        _ => org.Finance,
    };

    private static HashSet<string> EligibleByCheck(ApprovalLevel level, OrganisationSnapshot org, RequestFacts facts) =>
        [.. SlotOf(level, org).Where(c => ApproverEligibility.Check(level, c, facts).All(r => r.Passed)).Select(c => c.UserId)];

    [Theory]
    [MemberData(nameof(RoutingCases))]
    public void Eligibility_reasons_match_routing_decision(string total, string requester, int orgIndex)
    {
        var org = Orgs[orgIndex];
        var amount = Money.Zar(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture));
        var facts = new RequestFacts(amount, requester, org);

        ApprovalChain chain;
        try
        {
            chain = ApprovalRouting.Resolve(amount, requester, org, Thresholds);
        }
        catch (NoEligibleApproverException)
        {
            // Routing found nobody up to finance: the check must agree that no finance candidate passes every reason.
            EligibleByCheck(ApprovalLevel.Finance, org, facts).ShouldBeEmpty();
            return;
        }

        foreach (var step in chain.Steps)
        {
            // Everyone routing picked passes every reason, and everyone it skipped at that level fails one.
            EligibleByCheck(step.Level, org, facts).ShouldBe(step.ApproverIds, ignoreOrder: true, $"level {step.Level}");
            foreach (var covered in step.CoversLevels.Where(l => l < step.Level))
            {
                // A lower level the step covers was skipped (nobody eligible) or merged (the same single approver).
                var eligible = EligibleByCheck(covered, org, facts);
                (eligible.Count == 0 || eligible.SetEquals(step.ApproverIds)).ShouldBeTrue($"covered level {covered}: {string.Join(",", eligible)}");
            }
        }
    }

    [Fact]
    public void Requester_fails_not_requester()
    {
        var reasons = ApproverEligibility.Check(ApprovalLevel.Manager, Maria(), new RequestFacts(Money.Zar(37_260m), "maria", Org()));

        var reason = reasons.Single(r => r.Code == "not-requester");
        reason.Passed.ShouldBeFalse();
        reason.Text.ShouldBe("Separation of duties: Maria Naidoo is the requester");
        reasons.Where(r => r.Code != "not-requester").ShouldAllBe(r => r.Passed);
        reasons.Single(r => r.Code == "cost-center").Text.ShouldBe("Cost center CC-1 is assigned to Maria Naidoo");
        reasons.Single(r => r.Code == "limit").Text
            .ShouldBe($"{Money.Zar(37_260m).ToDisplayString()} is within the {Money.Zar(200_000m).ToDisplayString()} limit");
    }

    [Fact]
    public void Limit_below_total_fails_limit()
    {
        var reasons = ApproverEligibility.Check(ApprovalLevel.Manager, Maria(), new RequestFacts(Money.Zar(200_000.01m), "tom", Org()));

        var reason = reasons.Single(r => r.Code == "limit");
        reason.Passed.ShouldBeFalse();
        reason.Text.ShouldBe($"{Money.Zar(200_000.01m).ToDisplayString()} is above the {Money.Zar(200_000m).ToDisplayString()} limit");
        reasons.Single(r => r.Code == "not-requester").Passed.ShouldBeTrue();
    }

    [Fact]
    public void Limit_equal_to_total_passes()
    {
        ApproverEligibility.Check(ApprovalLevel.Manager, Maria(), new RequestFacts(Money.Zar(200_000m), "tom", Org()))
            .Single(r => r.Code == "limit").Passed.ShouldBeTrue();
    }

    [Fact]
    public void Each_level_checks_its_own_responsibility()
    {
        var facts = new RequestFacts(Money.Zar(5_000m), "tom", Org());

        var asHead = ApproverEligibility.Check(ApprovalLevel.DepartmentHead, Maria(), facts);
        var asFinance = ApproverEligibility.Check(ApprovalLevel.Finance, Maria(), facts);
        var thandi = ApproverEligibility.Check(ApprovalLevel.Finance, Thandi(), facts);

        asHead.Select(r => r.Code).ShouldBe(["permission", "department", "limit", "not-requester"]);
        asHead.Single(r => r.Code == "department").Passed.ShouldBeFalse();
        asHead.Single(r => r.Code == "department").Text.ShouldBe("Maria Naidoo does not head department IT (cost center CC-1)");
        asFinance.Single(r => r.Code == "finance").Passed.ShouldBeFalse();
        asFinance.Single(r => r.Code == "finance").Text.ShouldBe("Maria Naidoo is not a finance approver");
        thandi.ShouldAllBe(r => r.Passed);
        thandi.Single(r => r.Code == "finance").Text.ShouldBe("thandi is a finance approver");
    }

    [Fact]
    public void Without_the_approve_permission_fails_permission()
    {
        var reason = ApproverEligibility.Check(ApprovalLevel.Manager, Maria(canApprove: false), new RequestFacts(Money.Zar(5_000m), "tom", Org()))
            .Single(r => r.Code == "permission");

        reason.Passed.ShouldBeFalse();
        reason.Text.ShouldBe("Maria Naidoo has no role with requests.approve");
    }
}
