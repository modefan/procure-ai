using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Budgets;
using Wolverine;

namespace ProcureAi.Budgets.Application.Commands;

/// <summary>
/// A spend that no budget can take: the amount is not positive, or no budget ever held a reservation for the request. Retrying
/// cannot help, so the infrastructure sends the message straight to the error queue (see <c>ConfigureBudgets</c>).
/// </summary>
public sealed class SpendCannotBeBookedException(string message) : Exception(message);

public static class RecordSpendHandler
{
    /// <summary>
    /// Books a payment as spend on the budget that holds (or held) the reservation of the request (ruling 16), then publishes
    /// <c>BudgetSpent</c> through the outbox. A payment delivered twice is booked once and publishes nothing the second time.
    /// The budget row changes, so a spend that runs at the same time as another spend or a release of the same budget loses
    /// on its version and is retried (see the Wolverine policies of the infrastructure); the unique key (budget, payment)
    /// does the same for two copies of one payment.
    /// </summary>
    public static async Task Handle(
        RecordSpend command, IBudgetsDbContext db, IMessageBus bus, ICacheInvalidator cache, TimeProvider time,
        ILogger<RecordSpend> logger, CancellationToken cancellationToken)
    {
        if (command.Amount <= 0m)
        {
            logger.LogError(
                "Spend of {Amount} for payment {PaymentId} of request {RequestId} is not positive.",
                command.Amount, command.PaymentId, command.RequestId);
            throw new SpendCannotBeBookedException($"The spend of payment {command.PaymentId} must be greater than zero.");
        }

        var budget = await db.Budgets.IgnoreQueryFilters().Include(b => b.Reservations).Include(b => b.Spends).SingleOrDefaultAsync(
            b => b.TenantId == command.TenantId && b.Reservations.Any(r => r.RequestId == command.RequestId), cancellationToken);
        if (budget is null)
        {
            logger.LogError(
                "No budget ever held a reservation for request {RequestId} (payment {PaymentId}); the spend cannot be booked.",
                command.RequestId, command.PaymentId);
            throw new SpendCannotBeBookedException($"No budget holds a reservation for request {command.RequestId}.");
        }

        var now = time.GetUtcNow();
        var outcome = budget.RecordSpend(command.RequestId, command.PaymentId, Money.Zar(command.Amount), now);
        if (outcome.AlreadyRecorded)
        {
            return;
        }

        if (outcome.WithoutReservation)
        {
            logger.LogWarning(
                "Payment {PaymentId} of request {RequestId} was booked as spend without a reservation to draw from (released or none).",
                command.PaymentId, command.RequestId);
        }

        await cache.RemoveAfterCommitAsync([BudgetCacheTag.Tenant(command.TenantId)]);
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(new BudgetSpent(
            command.RequestId, command.TenantId, budget.CostCenterCode, command.PaymentId, command.Amount, now));
    }
}
