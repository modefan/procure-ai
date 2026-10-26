using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Budgets;

namespace ProcureAi.Budgets.Application;

/// <summary>What the handler must do with the per-request marker row (see <see cref="RequestMarker"/>).</summary>
public enum MarkerChange
{
    None,
    AddReserved,
    AddReleased,
    MarkReleased,
}

/// <summary>The outcome to publish for a reserve command and what to write for the request marker.</summary>
public sealed record ReserveDecision(IIntegrationEvent Event, MarkerChange Marker);

/// <summary>The event to publish for a release command (null when nothing was freed) and what to write for the marker.</summary>
public sealed record ReleaseDecision(BudgetReleased? Event, MarkerChange Marker);

/// <summary>
/// The decisions of the reserve and release handlers, without persistence, so they can be unit-tested (the handlers load the
/// budget and the marker, call these, save and publish).
/// </summary>
public static class BudgetDecisions
{
    public const string InvalidAmount = "Amount must be greater than zero";

    /// <summary>
    /// What a reserve command leads to. An amount that is not positive is refused with a reason (never thrown, a throw would
    /// only be retried and dead-lettered). A request released earlier (<paramref name="marker"/> says so) is refused as
    /// cancelled; a marker that says reserved means a repeated message, which repeats the earlier answer; no budget is a
    /// failure; otherwise the aggregate decides.
    /// </summary>
    public static ReserveDecision Reserve(
        ReserveBudget command, FiscalYear year, CostCenterBudget? budget, RequestMarker? marker, DateTimeOffset now)
    {
        if (command.Amount <= 0m)
        {
            return new ReserveDecision(Failed(command, InvalidAmount, 0m, now), MarkerChange.None);
        }

        if (marker is { IsReleased: true })
        {
            return new ReserveDecision(Failed(command, ReservationOutcome.Cancelled, 0m, now), MarkerChange.None);
        }

        if (budget is null)
        {
            return new ReserveDecision(Failed(command, $"No budget for {command.CostCenterCode} in {year}", 0m, now), MarkerChange.None);
        }

        var outcome = budget.Reserve(command.RequestId, Money.Zar(command.Amount));
        if (!outcome.IsReserved)
        {
            return new ReserveDecision(Failed(command, outcome.Reason!, outcome.Available.Amount, now), MarkerChange.None);
        }

        var reserved = new BudgetReserved(
            command.RequestId, command.TenantId, command.CostCenterCode, year.ToString(), outcome.Amount.Amount, now);
        return new ReserveDecision(reserved, marker is null ? MarkerChange.AddReserved : MarkerChange.None);
    }

    /// <summary>
    /// What a release command leads to. Without a marker the release came first: a released marker is stored so the
    /// reservation that follows is refused. A reserved marker means the request holds money in <paramref name="budget"/>:
    /// it is freed, <c>BudgetReleased</c> is published when money was freed, and the marker is marked released.
    /// </summary>
    public static ReleaseDecision Release(ReleaseBudget command, RequestMarker? marker, CostCenterBudget? budget, DateTimeOffset now)
    {
        if (marker is null)
        {
            return new ReleaseDecision(Event: null, MarkerChange.AddReleased);
        }

        if (marker.IsReleased)
        {
            return new ReleaseDecision(Event: null, MarkerChange.None);
        }

        var released = budget?.Release(command.RequestId) ?? Money.Zero;
        var freed = released.Amount > 0m
            ? new BudgetReleased(command.RequestId, command.TenantId, budget!.CostCenterCode, released.Amount, now)
            : null;
        return new ReleaseDecision(freed, MarkerChange.MarkReleased);
    }

    private static BudgetReservationFailed Failed(ReserveBudget command, string reason, decimal available, DateTimeOffset now) =>
        new(command.RequestId, command.TenantId, command.CostCenterCode, reason, command.Amount, available, now);
}
