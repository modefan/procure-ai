using JasperFx;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using Wolverine;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// For an HTTP command run in process (<c>IMessageBus.InvokeAsync</c>): a handler that lost a race in the database is retried
/// inline by <see cref="ConcurrencyRetryPolicy.RetryConcurrencyConflicts"/> and then answers with the winner's outcome. If even
/// the retries lose (heavy contention), the race must still reach the caller as 409 "try again", never as a 500.
/// </summary>
public static class ConcurrencyConflicts
{
    /// <summary>The Postgres error for a unique key violated by a concurrent insert.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// A lost race in the database: a concurrent change of a versioned row (<see cref="DbUpdateConcurrencyException"/>), of a saga
    /// (<see cref="ConcurrencyException"/>, which Wolverine's <c>SagaConcurrencyException</c> derives from) or a concurrent insert
    /// of the same key. Not a stale <c>If-Match</c> (<see cref="ConcurrencyConflictException"/>): that is the caller's answer.
    /// </summary>
    /// <remarks>
    /// Caveat: every Postgres <c>23505</c> counts, whichever unique index it came from, so a duplicate that is a plain data error
    /// (not a race) is also answered 409 "changed by someone else, try again". That is acceptable for the commands that use this
    /// (their only unique keys are the ones concurrent callers contend for); a command with another unique constraint must not
    /// rely on it.
    /// </remarks>
    public static bool IsLostRace(Exception exception) =>
        exception is DbUpdateConcurrencyException or ConcurrencyException
        || exception is DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } };

    /// <summary>Invokes <paramref name="command"/>; a race it still lost after its retries becomes a <see cref="ConflictException"/> (409).</summary>
    /// <param name="what">What was changed, for the message, e.g. <c>The approval</c>.</param>
    public static async Task InvokeOrConflictAsync(
        this IMessageBus bus, object command, string what, CancellationToken cancellationToken)
    {
        try
        {
            await bus.InvokeAsync(command, cancellationToken);
        }
        catch (Exception exception) when (IsLostRace(exception))
        {
            throw new ConflictException($"{what} was changed by someone else at the same time. Reload it and try again.");
        }
    }
}
