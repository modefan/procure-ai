using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Shouldly;

namespace ProcureAi.Budgets.Tests;

/// <summary>The reserve and release handlers decide through BudgetDecisions; persistence is covered by Task 6 integration tests.</summary>
public sealed class BudgetDecisionsTests
{
    private static readonly FiscalYear Year = new(2027);
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CostCenterBudget Budget(decimal amount = 1000m) =>
        CostCenterBudget.Create("demo", "CC-100", Year, Money.Zar(amount));

    private static ReserveBudget Reserve(Guid id, decimal amount) => new(id, "demo", "CC-100", amount, Now);

    [Fact]
    public void Release_before_reserve_asks_for_a_tenant_tombstone_and_publishes_nothing()
    {
        var decision = BudgetDecisions.Release(new ReleaseBudget(Guid.NewGuid(), "demo"), budget: null, Now);

        decision.RecordTombstone.ShouldBeTrue();
        decision.Event.ShouldBeNull();
    }

    [Fact]
    public void Reserve_after_a_tenant_tombstone_is_refused_as_cancelled_even_when_money_is_available()
    {
        var budget = Budget();
        var id = Guid.NewGuid();

        var result = BudgetDecisions.Reserve(Reserve(id, 100m), Year, budget, releasedEarlier: true, Now);

        var failed = result.ShouldBeOfType<BudgetReservationFailed>();
        failed.Reason.ShouldBe("Cancelled");
        failed.RequestId.ShouldBe(id);
        budget.Reserved.ShouldBe(Money.Zero);
    }

    [Fact]
    public void Reserve_publishes_BudgetReserved_in_the_FY_format()
    {
        var result = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 250m), Year, Budget(), releasedEarlier: false, Now);

        var reserved = result.ShouldBeOfType<BudgetReserved>();
        reserved.FiscalYear.ShouldBe("FY2027");
        reserved.Amount.ShouldBe(250m);
    }

    [Fact]
    public void Reserve_over_budget_publishes_the_failure_with_available()
    {
        var result = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 1000.01m), Year, Budget(), releasedEarlier: false, Now);

        var failed = result.ShouldBeOfType<BudgetReservationFailed>();
        failed.Reason.ShouldBe("Over budget");
        failed.Available.ShouldBe(1000m);
        failed.Requested.ShouldBe(1000.01m);
    }

    [Fact]
    public void Reserve_without_a_budget_names_the_cost_center_and_year()
    {
        var result = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 10m), Year, budget: null, releasedEarlier: false, Now);

        result.ShouldBeOfType<BudgetReservationFailed>().Reason.ShouldBe("No budget for CC-100 in FY2027");
    }

    [Fact]
    public void Release_publishes_BudgetReleased_only_when_money_was_freed()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        BudgetDecisions.Reserve(Reserve(id, 300m), Year, budget, releasedEarlier: false, Now);

        var first = BudgetDecisions.Release(new ReleaseBudget(id, "demo"), budget, Now);
        var second = BudgetDecisions.Release(new ReleaseBudget(id, "demo"), budget, Now);

        first.Event.ShouldNotBeNull().Amount.ShouldBe(300m);
        first.RecordTombstone.ShouldBeFalse();
        second.Event.ShouldBeNull();
        second.RecordTombstone.ShouldBeFalse();
    }
}
