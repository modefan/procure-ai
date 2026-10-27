using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Errors;
using Wolverine;
using Wolverine.ErrorHandling;

namespace ProcureAi.BuildingBlocks.Messaging;

public static class DependencyRetryPolicy
{
    /// <summary>
    /// Retries a handler whose call to another service failed for a reason that passes: the call failed
    /// (<see cref="HttpRequestException"/>, e.g. 503 or connection refused) or the resilience pipeline refused it
    /// (<see cref="ExecutionRejectedException"/>: timeout, open circuit). The handler's transaction rolled back, so the retry
    /// runs it again from the start. Retries are scheduled (stored durably, the listener moves on) with the delays of
    /// <paramref name="retry"/>. After the last one the message goes to the dead letters, where Wolverine logs it ("moved to the
    /// error queue") and counts it (metric <c>wolverine-dead-letter-queue</c>); it can be replayed from there once the dependency
    /// is back. An answer such as "not found" is not a failure and is not retried.
    /// </summary>
    /// <param name="section">The configuration path of <paramref name="retry"/>, for the message when it is not valid.</param>
    public static void RetryTransientDependencyFailures(this WolverineOptions options, DependencyRetryOptions retry, string section)
    {
        var delays = retry.Delays(section);
        options.OnException(IsTransientDependencyFailure, "a call to another service failed")
            .ScheduleRetry(delays)
            .Then.MoveToErrorQueue();
    }

    /// <summary>True when <paramref name="exception"/>, or one it wraps, is a failed or refused call to another service.</summary>
    public static bool IsTransientDependencyFailure(Exception exception) =>
        DependencyFailures.IsTransient(exception);
}
