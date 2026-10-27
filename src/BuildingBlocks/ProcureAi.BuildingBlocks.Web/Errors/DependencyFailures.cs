using System.Net;
using Polly;

namespace ProcureAi.BuildingBlocks.Web.Errors;

/// <summary>Which exceptions mean "a service we depend on is not answering right now" (as opposed to a bug or a business answer).</summary>
public static class DependencyFailures
{
    /// <summary>
    /// True when <paramref name="exception"/>, or one it wraps, is a failed or refused call to another service: the call failed
    /// (<see cref="HttpRequestException"/>) or the resilience pipeline refused it (<see cref="ExecutionRejectedException"/>:
    /// timeout, open circuit). An HTTP failure counts only when it may pass by itself: no status code (connection refused,
    /// DNS, reset), 408, 429 or 5xx. A 400, 401, 403, 404 or 409 is an answer that a retry will not change, so it is not
    /// "the service is down" (and must not become a 503 or a saga retry).
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ExecutionRejectedException
                || (current is HttpRequestException http && IsTransientStatus(http.StatusCode)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientStatus(HttpStatusCode? status) =>
        status is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
