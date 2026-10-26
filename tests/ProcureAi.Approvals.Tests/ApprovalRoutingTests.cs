using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Approvals.Tests;

public class ApprovalRoutingTests
{
    private static readonly ApprovalThresholds Thresholds = new(Money.Zar(20_000m), Money.Zar(200_000m));

    private static Candidate Maria(bool canApprove = true) => new("maria", Money.Zar(200_000m), canApprove);
    private static Candidate Dept() => new("dept", Money.Zar(500_000m), true);
    private static Candidate Thandi() => new("thandi", Money.Zar(2_000_000m), true);

    private static OrganisationSnapshot Org(Candidate? manager = null, Candidate? dept = null, params Candidate[] finance) =>
        new("CC-1", manager ?? Maria(), dept ?? Dept(), finance.Length == 0 ? [Thandi()] : finance);

    private static string Shape(ApprovalChain chain) =>
        string.Join(" | ", chain.Steps.Select(s =>
            $"{s.StepNo}:{s.Level}[{string.Join("+", s.CoversLevels)}]={string.Join(",", s.ApproverIds)}"));

    [Theory]
    [InlineData("5000", "tom", "1:Manager[Manager]=maria")]
    [InlineData("30000", "tom", "1:Manager[Manager]=maria | 2:DepartmentHead[DepartmentHead]=dept")]
    [InlineData("250000", "tom", "1:DepartmentHead[Manager+DepartmentHead]=dept | 2:Finance[Finance]=thandi")]
    [InlineData("30000", "maria", "1:DepartmentHead[Manager+DepartmentHead]=dept")]
    [InlineData("600000", "tom", "1:Finance[Manager+DepartmentHead+Finance]=thandi")]
    [InlineData("20000.00", "tom", "1:Manager[Manager]=maria")]
    [InlineData("20000.01", "tom", "1:Manager[Manager]=maria | 2:DepartmentHead[DepartmentHead]=dept")]
    [InlineData("200000.00", "tom", "1:Manager[Manager]=maria | 2:DepartmentHead[DepartmentHead]=dept")]
    [InlineData("200000.01", "tom", "1:DepartmentHead[Manager+DepartmentHead]=dept | 2:Finance[Finance]=thandi")]
    public void Resolve_builds_the_expected_chain(string total, string requester, string expected)
    {
        var chain = ApprovalRouting.Resolve(
            Money.Zar(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture)), requester, Org(), Thresholds);

        Shape(chain).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_throws_when_finance_has_nobody_but_the_requester()
    {
        var ex = Should.Throw<NoEligibleApproverException>(() =>
            ApprovalRouting.Resolve(Money.Zar(250_000m), "thandi", Org(), Thresholds));

        ex.Level.ShouldBe(ApprovalLevel.Finance);
        ex.ShouldBeAssignableTo<DomainException>();
    }

    [Fact]
    public void Resolve_moves_up_when_the_manager_cannot_approve()
    {
        var chain = ApprovalRouting.Resolve(Money.Zar(5_000m), "tom", Org(manager: Maria(canApprove: false)), Thresholds);

        Shape(chain).ShouldBe("1:DepartmentHead[Manager]=dept");
    }

    [Fact]
    public void Resolve_moves_up_when_the_manager_is_missing()
    {
        var org = new OrganisationSnapshot("CC-1", null, Dept(), [Thandi()]);

        Shape(ApprovalRouting.Resolve(Money.Zar(5_000m), "tom", org, Thresholds)).ShouldBe("1:DepartmentHead[Manager]=dept");
    }

    [Fact]
    public void Resolve_lists_all_eligible_finance_approvers_ordered_by_limit_then_user_id()
    {
        var finance = new[]
        {
            new Candidate("zed", Money.Zar(3_000_000m), true),
            new Candidate("bob", Money.Zar(2_000_000m), true),
            new Candidate("amy", Money.Zar(2_000_000m), true),
            new Candidate("low", Money.Zar(100_000m), true),
            new Candidate("off", Money.Zar(9_000_000m), false),
            new Candidate("tom", Money.Zar(9_000_000m), true),
        };

        var chain = ApprovalRouting.Resolve(Money.Zar(600_000m), "tom", Org(finance: finance), Thresholds);

        Shape(chain).ShouldBe("1:Finance[Manager+DepartmentHead+Finance]=amy,bob,zed");
    }

    [Fact]
    public void Resolve_throws_when_nothing_can_cover_the_amount()
    {
        Should.Throw<NoEligibleApproverException>(() =>
            ApprovalRouting.Resolve(Money.Zar(5_000_000m), "tom", Org(), Thresholds))
            .Level.ShouldBe(ApprovalLevel.Finance);
    }

    [Fact]
    public void Resolve_never_includes_the_requester()
    {
        var chain = ApprovalRouting.Resolve(Money.Zar(250_000m), "dept", Org(), Thresholds);

        chain.Steps.SelectMany(s => s.ApproverIds).ShouldNotContain("dept");
        Shape(chain).ShouldBe("1:Finance[Manager+DepartmentHead+Finance]=thandi");
    }

    private static string Shape(PlannedStep? step) =>
        step is null ? "null" : $"{step.StepNo}:{step.Level}[{string.Join("+", step.CoversLevels)}]={string.Join(",", step.ApproverIds)}";

    [Fact]
    public void Escalate_from_manager_replaces_the_step_with_the_department_head()
    {
        var from = new PlannedStep(1, ApprovalLevel.Manager, [ApprovalLevel.Manager], ["maria"]);

        var next = ApprovalRouting.Escalate(from, Money.Zar(5_000m), "tom", Org(), []);

        Shape(next).ShouldBe("1:DepartmentHead[Manager+DepartmentHead]=dept");
    }

    [Fact]
    public void Escalate_in_the_middle_of_a_chain_keeps_the_step_number_and_the_covered_levels()
    {
        var from = new PlannedStep(1, ApprovalLevel.Manager, [ApprovalLevel.Manager], ["maria"]);

        var next = ApprovalRouting.Escalate(from, Money.Zar(30_000m), "tom", Org(), []);

        Shape(next).ShouldBe("1:DepartmentHead[Manager+DepartmentHead]=dept");
    }

    [Fact]
    public void Escalate_from_department_head_skips_to_finance_when_dept_is_the_requester()
    {
        var from = new PlannedStep(2, ApprovalLevel.DepartmentHead, [ApprovalLevel.DepartmentHead], ["dept"]);

        var next = ApprovalRouting.Escalate(from, Money.Zar(30_000m), "dept", Org(), []);

        Shape(next).ShouldBe("2:Finance[DepartmentHead+Finance]=thandi");
    }

    [Fact]
    public void Escalate_from_a_merged_step_keeps_all_its_covered_levels()
    {
        var from = new PlannedStep(1, ApprovalLevel.DepartmentHead, [ApprovalLevel.Manager, ApprovalLevel.DepartmentHead], ["dept"]);

        var next = ApprovalRouting.Escalate(from, Money.Zar(250_000m), "tom", Org(), []);

        Shape(next).ShouldBe("1:Finance[Manager+DepartmentHead+Finance]=thandi");
    }

    [Fact]
    public void Escalate_from_finance_returns_null()
    {
        var from = new PlannedStep(1, ApprovalLevel.Finance, [ApprovalLevel.Finance], ["thandi"]);

        Shape(ApprovalRouting.Escalate(from, Money.Zar(30_000m), "tom", Org(), [])).ShouldBe("null");
    }

    [Fact]
    public void Escalate_returns_null_when_nobody_above_is_eligible()
    {
        var from = new PlannedStep(1, ApprovalLevel.DepartmentHead, [ApprovalLevel.DepartmentHead], ["dept"]);

        Shape(ApprovalRouting.Escalate(from, Money.Zar(30_000m), "thandi", Org(), [])).ShouldBe("null");
    }

    [Fact]
    public void Escalate_skips_a_level_whose_only_approver_already_approved()
    {
        var from = new PlannedStep(1, ApprovalLevel.Manager, [ApprovalLevel.Manager], ["maria"]);

        // Without alreadyApproved the escalation would land on dept.
        Shape(ApprovalRouting.Escalate(from, Money.Zar(5_000m), "tom", Org(), [])).ShouldBe("1:DepartmentHead[Manager+DepartmentHead]=dept");

        var next = ApprovalRouting.Escalate(from, Money.Zar(5_000m), "tom", Org(), ["dept"]);

        Shape(next).ShouldBe("1:Finance[Manager+DepartmentHead+Finance]=thandi");
    }

    [Fact]
    public void Escalate_to_finance_excludes_only_the_requester_and_those_who_already_approved()
    {
        var finance = new[]
        {
            new Candidate("amy", Money.Zar(2_000_000m), true),
            new Candidate("bob", Money.Zar(2_000_000m), true),
            new Candidate("cat", Money.Zar(2_000_000m), true),
            new Candidate("tom", Money.Zar(2_000_000m), true),
        };
        var from = new PlannedStep(2, ApprovalLevel.DepartmentHead, [ApprovalLevel.DepartmentHead], ["dept"]);

        var next = ApprovalRouting.Escalate(from, Money.Zar(30_000m), "tom", Org(finance: finance), ["amy"]);

        Shape(next).ShouldBe("2:Finance[DepartmentHead+Finance]=bob,cat");
    }
}
