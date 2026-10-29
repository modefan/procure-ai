using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

public static class ReserveBudgetHandler
{
    /// <summary>
    /// Reserves the amount in the budget of the fiscal year the request was submitted in, then publishes the outcome through
    /// the outbox. A message delivered twice reserves once and repeats the same answer. A request released earlier is refused
    /// as cancelled. Two things serialise concurrent work in the database, and the loser is retried (see the Wolverine
    /// policies of the infrastructure): the budget row changes on every reservation (its version rejects the second of two
    /// reservations), and the request marker row has a unique key (a reserve and a release of one request cannot both insert it).
    /// The command carries the tenant, so the queries name it instead of relying on the caller headers of the message.
    /// </summary>
    public static async Task Handle(
        ReserveBudget command, IBudgetsDbContext db, IMessageBus bus, ICacheInvalidator cache, IOptions<BudgetsOptions> options,
        TimeProvider time, ILogger<ReserveBudget> logger, CancellationToken cancellationToken)
    {
        var year = FiscalYear.For(command.SubmittedAt, options.Value.Zone);
        var marker = await db.RequestMarkers.IgnoreQueryFilters().SingleOrDefaultAsync(
            m => m.TenantId == command.TenantId && m.RequestId == command.RequestId, cancellationToken);
        // A repeated command (the marker says reserved) is answered from the budget that holds the reservation, never from
        // the cost center the command names.
        var budget = marker switch
        {
            { IsReleased: true } => null,
            { IsReleased: false } => await db.Budgets.IgnoreQueryFilters().Include(b => b.Reservations).SingleOrDefaultAsync(
                b => b.TenantId == command.TenantId && b.Reservations.Any(r => r.RequestId == command.RequestId), cancellationToken),
            null => await db.Budgets.IgnoreQueryFilters().Include(b => b.Reservations).SingleOrDefaultAsync(
                b => b.TenantId == command.TenantId && b.CostCenterCode == command.CostCenterCode && b.FiscalYear == year,
                cancellationToken),
        };
        if (marker is { IsReleased: false } && budget is not null
            && (budget.CostCenterCode != command.CostCenterCode || budget.FiscalYear != year))
        {
            logger.LogError(
                "Request {RequestId} is reserved on {HeldCostCenter} {HeldYear} but the command names {CostCenter} {Year}; answering from the holding budget.",
                command.RequestId, budget.CostCenterCode, budget.FiscalYear, command.CostCenterCode, year);
        }

        var decision = BudgetDecisions.Reserve(command, year, budget, marker, time.GetUtcNow());
        if (decision.Marker == MarkerChange.AddReserved)
        {
            db.RequestMarkers.Add(RequestMarker.Reserved(command.TenantId, command.RequestId));
            // Cached budget reads of the tenant are dropped once this handler's transaction has committed.
            await cache.RemoveAfterCommitAsync([BudgetCacheTag.Tenant(command.TenantId)]);
        }

        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(decision.Event);
    }
}
