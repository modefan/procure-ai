using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Budgets.Tests;

public sealed class CostCenterBudgetTests
{
    private static CostCenterBudget Budget(decimal amount = 1000m) =>
        CostCenterBudget.Create("demo", "CC-100", new FiscalYear(2027), Money.Zar(amount));

    [Fact]
    public void Reserve_within_available_succeeds()
    {
        var budget = Budget();
        var outcome = budget.Reserve(Guid.NewGuid(), Money.Zar(400m));

        outcome.IsReserved.ShouldBeTrue();
        outcome.Amount.ShouldBe(Money.Zar(400m));
        budget.Reserved.ShouldBe(Money.Zar(400m));
        budget.Available.ShouldBe(Money.Zar(600m));
    }

    [Fact]
    public void Reserve_exactly_the_available_amount_succeeds()
    {
        Budget().Reserve(Guid.NewGuid(), Money.Zar(1000m)).IsReserved.ShouldBeTrue();
    }

    [Fact]
    public void Reserve_over_by_one_cent_is_refused_with_the_available_amount()
    {
        var budget = Budget();
        var outcome = budget.Reserve(Guid.NewGuid(), Money.Zar(1000.01m));

        outcome.IsReserved.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReservationOutcome.OverBudget);
        outcome.Available.ShouldBe(Money.Zar(1000m));
        budget.Reserved.ShouldBe(Money.Zero);
    }

    [Fact]
    public void The_same_request_twice_reserves_once()
    {
        var budget = Budget();
        var id = Guid.NewGuid();

        var first = budget.Reserve(id, Money.Zar(400m));
        var second = budget.Reserve(id, Money.Zar(400m));

        second.ShouldBe(first);
        budget.Reserved.ShouldBe(Money.Zar(400m));
    }

    [Fact]
    public void Release_returns_the_amount_and_frees_it()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        budget.Reserve(id, Money.Zar(400m));

        budget.Release(id).ShouldBe(Money.Zar(400m));
        budget.Reserved.ShouldBe(Money.Zero);
        budget.Available.ShouldBe(Money.Zar(1000m));
    }

    [Fact]
    public void Release_twice_returns_zero_the_second_time()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        budget.Reserve(id, Money.Zar(400m));
        budget.Release(id);

        budget.Release(id).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Release_of_an_unknown_request_returns_zero_and_leaves_a_tombstone()
    {
        var budget = Budget();
        var id = Guid.NewGuid();

        budget.Release(id).ShouldBe(Money.Zero);
        budget.Reservations.ShouldContain(r => r.RequestId == id && r.IsReleased);
    }

    [Fact]
    public void Reserve_after_release_is_refused_as_cancelled()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        budget.Reserve(id, Money.Zar(100m));
        budget.Release(id);

        var outcome = budget.Reserve(id, Money.Zar(100m));

        outcome.IsReserved.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReservationOutcome.Cancelled);
        budget.Reserved.ShouldBe(Money.Zero);
    }

    [Fact]
    public void Reserve_after_a_release_that_came_first_is_refused_as_cancelled()
    {
        var budget = Budget();
        var id = Guid.NewGuid();
        budget.Release(id);

        budget.Reserve(id, Money.Zar(100m)).Reason.ShouldBe(ReservationOutcome.Cancelled);
    }

    [Fact]
    public void SetAmount_below_what_is_committed_is_refused()
    {
        var budget = Budget();
        budget.Reserve(Guid.NewGuid(), Money.Zar(400m));

        Should.Throw<DomainException>(() => budget.SetAmount(Money.Zar(399.99m)));
        budget.Amount.ShouldBe(Money.Zar(1000m));
    }

    [Fact]
    public void SetAmount_to_exactly_what_is_committed_is_allowed()
    {
        var budget = Budget();
        budget.Reserve(Guid.NewGuid(), Money.Zar(400m));

        budget.SetAmount(Money.Zar(400m));

        budget.Available.ShouldBe(Money.Zero);
    }

    [Fact]
    public void Released_money_can_be_reserved_by_another_request()
    {
        var budget = Budget();
        var first = Guid.NewGuid();
        budget.Reserve(first, Money.Zar(1000m));
        budget.Reserve(Guid.NewGuid(), Money.Zar(1m)).IsReserved.ShouldBeFalse();
        budget.Release(first);

        budget.Reserve(Guid.NewGuid(), Money.Zar(1m)).IsReserved.ShouldBeTrue();
    }

    [Fact]
    public void A_non_Rand_amount_is_refused()
    {
        Should.Throw<DomainException>(() => Budget().Reserve(Guid.NewGuid(), new Money(10m, "USD")));
    }
}
