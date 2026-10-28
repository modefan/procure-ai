using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Errors;
using Wolverine;
using Wolverine.ErrorHandling;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// Retries a message handler whose database (or another piece of infrastructure) failed for a reason that passes by itself: a
/// transient Npgsql failure (<see cref="NpgsqlException.IsTransient"/>: connection reset or refused, I/O error, timeout, and the
/// Postgres errors Npgsql calls transient such as a serialization failure or a deadlock) or a <see cref="TimeoutException"/>,
/// thrown directly or wrapped (EF Core's <c>DbUpdateException</c>, Marten's command exceptions). Without it Wolverine 6.53 moves the
/// message to the error queue on its first failure, and a single connection reset at commit strands a spend, a supplier send or an
/// ERP export until someone replays the dead letters by hand.
/// <para>
/// Retries are scheduled (durable inbox, the listener moves on) with the delays of <see cref="SectionName"/>, and after the last one
/// the message goes to the error queue. The handler's transaction rolled back, so the retry runs it again from the start.
/// </para>
/// <para>
/// It is set for every service in <see cref="MessagingSetup.UseProcureAiHandlers"/>, before the service's own rules. It leaves the
/// failures those rules own alone, so it cannot shadow them whatever the order: lost races (concurrency and unique violations,
/// which are not transient to Npgsql) and failed calls to other services (<see cref="DependencyFailures.IsTransient"/>, retried on
/// the service's <c>DependencyRetry</c> schedule).
/// </para>
/// </summary>
public static class TransientDatabaseRetryPolicy
{
    public const string SectionName = "Messaging:TransientDatabaseRetry";

    /// <summary>The defaults (1 s, 5 s, 25 s, 2 min, then the error queue); a service can override any of them in <see cref="SectionName"/>.</summary>
    public static DependencyRetryOptions Defaults() => new()
    {
        Attempts = 4,
        InitialDelay = TimeSpan.FromSeconds(1),
        Multiplier = 5.0,
        MaxDelay = TimeSpan.FromMinutes(2),
    };

    /// <summary>The configured delays, validated (an invalid section fails at startup).</summary>
    public static TimeSpan[] Delays(IConfiguration configuration)
    {
        var retry = Defaults();
        configuration.GetSection(SectionName).Bind(retry);
        return retry.Delays(SectionName);
    }

    /// <summary>Adds the rule: scheduled retries with <paramref name="delays"/>, then the error queue.</summary>
    public static WolverineOptions RetryTransientDatabaseFailures(this WolverineOptions options, TimeSpan[] delays)
    {
        options.OnException(IsTransientDatabaseFailure, "the database or another piece of infrastructure failed for a passing reason")
            .ScheduleRetry(delays)
            .Then.MoveToErrorQueue();
        return options;
    }

    /// <summary>
    /// True when <paramref name="exception"/>, or one it wraps, is a transient <see cref="NpgsqlException"/> or a
    /// <see cref="TimeoutException"/>, and it is not a failed call to another service (that has a rule of its own).
    /// </summary>
    public static bool IsTransientDatabaseFailure(Exception exception)
    {
        if (DependencyFailures.IsTransient(exception))
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
