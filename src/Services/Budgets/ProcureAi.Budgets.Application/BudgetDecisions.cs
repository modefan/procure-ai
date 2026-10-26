using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Budgets;

namespace ProcureAi.Budgets.Application;

/// <summary>
/// The decisions of the reserve and release handlers, without persistence, so they can be unit-tested (the handlers load the
/// budget and the tombstone, call these, save and publish).
/// </summary>
public static class BudgetDecisions
{
    /// <summary>
    /// What a reserve command leads to. A request released earlier (<paramref name="releasedEarlier"/>, the tenant-level
    /// tombstone) is refused as cancelled before anything else; no budget is a failure; otherwise the aggregate decides.
    /// </summary>
    public static IIntegrationEvent Reserve(
        ReserveBudget command, FiscalYear year, CostCenterBudget? budget, bool releasedEarlier, DateTimeOffset now)
    {
        if (releasedEarlier)
        {
            return Failed(command, ReservationOutcome.Cancelled, 0m, now);
        }

        if (budget is null)
        {
            return Failed(command, $"No budget for {command.CostCenterCode} in {year}", 0m, now);
        }

        var outcome = budget.Reserve(command.RequestId, Money.Zar(command.Amount));
        return outcome.IsReserved
            ? new BudgetReserved(
                command.RequestId, command.TenantId, command.CostCenterCode, year.ToString(), outcome.Amount.Amount, now)
            : Failed(command, outcome.Reason!, outcome.Available.Amount, now);
    }

    /// <summary>
    /// What a release command leads to. With a budget holding the request: the release, and <c>BudgetReleased</c> only when
    /// money was freed. Without one: the release came first, so the caller must store a <see cref="ReleasedRequest"/>.
    /// </summary>
    public static ReleaseDecision Release(ReleaseBudget command, CostCenterBudget? budget, DateTimeOffset now)
    {
        if (budget is null)
        {
            return new ReleaseDecision(Event: null, RecordTombstone: true);
        }

        var released = budget.Release(command.RequestId);
        return released.Amount > 0m
            ? new ReleaseDecision(
                new BudgetReleased(command.RequestId, command.TenantId, budget.CostCenterCode, released.Amount, now), false)
            : new ReleaseDecision(null, false);
    }

    private static BudgetReservationFailed Failed(ReserveBudget command, string reason, decimal available, DateTimeOffset now) =>
        new(command.RequestId, command.TenantId, command.CostCenterCode, reason, command.Amount, available, now);
}

public sealed record ReleaseDecision(BudgetReleased? Event, bool RecordTombstone);
