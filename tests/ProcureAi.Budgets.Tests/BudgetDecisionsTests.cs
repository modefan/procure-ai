using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Shouldly;

namespace ProcureAi.Budgets.Tests;

/// <summary>The reserve and release handlers decide through BudgetDecisions; persistence is covered by the integration tests.</summary>
public sealed class BudgetDecisionsTests
{
    private static readonly FiscalYear Year = new(2027);
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CostCenterBudget Budget(decimal amount = 1000m) =>
        CostCenterBudget.Create("demo", "CC-100", Year, Money.Zar(amount));

    private static ReserveBudget Reserve(Guid id, decimal amount) => new(id, "demo", "CC-100", amount, Now);

    [Fact]
    public void Release_before_reserve_asks_for_a_released_marker_and_publishes_nothing()
    {
        var decision = BudgetDecisions.Release(new ReleaseBudget(Guid.NewGuid(), "demo"), marker: null, budget: null, Now);

        decision.Marker.ShouldBe(MarkerChange.AddReleased);
        decision.Event.ShouldBeNull();
    }

    [Fact]
    public void Reserve_after_a_released_marker_is_refused_as_cancelled_even_when_money_is_available()
    {
        var budget = Budget();
        var id = Guid.NewGuid();

        var decision = BudgetDecisions.Reserve(Reserve(id, 100m), Year, budget, RequestMarker.Released("demo", id), Now);

        var failed = decision.Event.ShouldBeOfType<BudgetReservationFailed>();
        failed.Reason.ShouldBe("Cancelled");
        failed.RequestId.ShouldBe(id);
        decision.Marker.ShouldBe(MarkerChange.None);
        budget.Reserved.ShouldBe(Money.Zero);
    }

    [Fact]
    public void Reserve_publishes_BudgetReserved_in_the_FY_format_and_asks_for_a_marker()
    {
        var decision = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 250m), Year, Budget(), marker: null, Now);

        var reserved = decision.Event.ShouldBeOfType<BudgetReserved>();
        reserved.FiscalYear.ShouldBe("FY2027");
        reserved.Amount.ShouldBe(250m);
        decision.Marker.ShouldBe(MarkerChange.AddReserved);
    }

    [Fact]
    public void Reserve_again_with_a_reserved_marker_repeats_the_answer_and_adds_nothing()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        BudgetDecisions.Reserve(Reserve(id, 250m), Year, budget, marker: null, Now);

        var again = BudgetDecisions.Reserve(Reserve(id, 250m), Year, budget, RequestMarker.Reserved("demo", id), Now);

        again.Event.ShouldBeOfType<BudgetReserved>().Amount.ShouldBe(250m);
        again.Marker.ShouldBe(MarkerChange.None);
        budget.Reserved.Amount.ShouldBe(250m);
    }

    [Fact]
    public void Reserve_over_budget_publishes_the_failure_with_available_and_writes_no_marker()
    {
        var decision = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 1000.01m), Year, Budget(), marker: null, Now);

        var failed = decision.Event.ShouldBeOfType<BudgetReservationFailed>();
        failed.Reason.ShouldBe("Over budget");
        failed.Available.ShouldBe(1000m);
        failed.Requested.ShouldBe(1000.01m);
        decision.Marker.ShouldBe(MarkerChange.None);
    }

    [Fact]
    public void Reserve_without_a_budget_names_the_cost_center_and_year()
    {
        var decision = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), 10m), Year, budget: null, marker: null, Now);

        decision.Event.ShouldBeOfType<BudgetReservationFailed>().Reason.ShouldBe("No budget for CC-100 in FY2027");
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void Reserve_with_an_amount_that_is_not_positive_is_refused_with_a_reason_and_never_throws(int amount)
    {
        var budget = Budget();

        var decision = BudgetDecisions.Reserve(Reserve(Guid.NewGuid(), amount), Year, budget, marker: null, Now);

        decision.Event.ShouldBeOfType<BudgetReservationFailed>().Reason.ShouldBe(BudgetDecisions.InvalidAmount);
        decision.Marker.ShouldBe(MarkerChange.None);
        budget.Reserved.ShouldBe(Money.Zero);
    }

    [Fact]
    public void Release_publishes_BudgetReleased_only_when_money_was_freed()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        BudgetDecisions.Reserve(Reserve(id, 300m), Year, budget, marker: null, Now);
        var marker = RequestMarker.Reserved("demo", id);

        var first = BudgetDecisions.Release(new ReleaseBudget(id, "demo"), marker, budget, Now);
        marker.Release();
        var second = BudgetDecisions.Release(new ReleaseBudget(id, "demo"), marker, budget, Now);

        first.Event.ShouldNotBeNull().Amount.ShouldBe(300m);
        first.Marker.ShouldBe(MarkerChange.MarkReleased);
        second.Event.ShouldBeNull();
        second.Marker.ShouldBe(MarkerChange.None);
        budget.Reserved.ShouldBe(Money.Zero);
    }
}
