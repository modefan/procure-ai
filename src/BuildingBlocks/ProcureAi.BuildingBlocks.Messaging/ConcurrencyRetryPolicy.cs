using JasperFx;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using Wolverine;
using Wolverine.ErrorHandling;

namespace ProcureAi.BuildingBlocks.Messaging;

public static class ConcurrencyRetryPolicy
{
    /// <summary>The Postgres error for a unique key violated by a concurrent insert.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Retries a handler that lost a race in the database: a concurrent change of a versioned row
    /// (<see cref="DbUpdateConcurrencyException"/>), a concurrent change of a saga (<see cref="ConcurrencyException"/>, which
    /// Wolverine's <c>SagaConcurrencyException</c> derives from) or a concurrent insert of the same key (unique violation).
    /// Inline retries with a fresh random jitter added at every failure; after the last attempt the message goes to the
    /// dead-letter queue. (Scheduled retries were tried in Budgets and rejected: they come back in lumps at the scheduled-job
    /// polling interval, so a burst re-collided each time. Waiting inline also caps the contenders at the listener parallelism.)
    /// The retried handler runs again from the start, so it sees the winner's outcome.
    /// </summary>
    /// <param name="options">The whole service (<see cref="WolverineOptions"/>) or one handler chain (rules scoped to its messages).</param>
    /// <param name="section">The configuration path of <paramref name="retry"/>, for the message when it is not valid.</param>
    public static void RetryConcurrencyConflicts(this IWithFailurePolicies options, ConcurrencyRetryOptions retry, string section)
    {
        var delays = retry.Delays(section);
        InlineRetry(options.OnException<DbUpdateConcurrencyException>(), delays, retry.JitterFraction);
        InlineRetry(options.OnException<ConcurrencyException>(), delays, retry.JitterFraction);
        InlineRetry(
            options.OnException<DbUpdateException>(exception => exception.InnerException is PostgresException { SqlState: UniqueViolation }),
            delays,
            retry.JitterFraction);
    }

    private static void InlineRetry(PolicyExpression when, TimeSpan[] delays, double jitterFraction)
    {
        var retries = when.RetryWithCooldown(delays);
        // Wolverine rejects a jitter of zero; zero means none.
        (jitterFraction > 0 ? retries.WithBoundedJitter(jitterFraction) : retries).Then.MoveToErrorQueue();
    }
}
