using Polly;

namespace ProcureAi.BuildingBlocks.Web.Errors;

/// <summary>Which exceptions mean "a service we depend on is not answering right now" (as opposed to a bug or a business answer).</summary>
public static class DependencyFailures
{
    /// <summary>
    /// True when <paramref name="exception"/>, or one it wraps, is a failed or refused call to another service: the call failed
    /// (<see cref="HttpRequestException"/>, e.g. 503 or connection refused) or the resilience pipeline refused it
    /// (<see cref="ExecutionRejectedException"/>: timeout, open circuit).
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or ExecutionRejectedException)
            {
                return true;
            }
        }

        return false;
    }
}
