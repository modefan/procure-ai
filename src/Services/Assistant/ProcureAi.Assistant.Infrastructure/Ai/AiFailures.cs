using System.ClientModel;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Errors;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// A model provider failure that may pass by itself: the call failed (<see cref="HttpRequestException"/> without a status, 408, 429 or
/// 5xx, or a refused resilience call), the OpenAI SDK answered 429 or 5xx (<see cref="ClientResultException"/>; status 0 means no
/// answer), or it timed out (<see cref="TimeoutException"/>, or a <see cref="TaskCanceledException"/> the caller did not ask for, which
/// the ingestion checks). Anything else (400, 401, a bug) is not retried. Applied only to exceptions thrown by a model call (the ingestion
/// wraps those calls), so a database timeout elsewhere is never taken for the model.
/// </summary>
internal sealed class AiFailures : IAiFailures
{
    public bool IsTransient(Exception exception)
    {
        if (DependencyFailures.IsTransient(exception))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClientResultException { Status: 0 or 408 or 429 or >= 500 }:
                case TimeoutException:
                case TaskCanceledException:
                    return true;
            }
        }

        return false;
    }
}

/// <summary>The shared transient-database rule (<see cref="TransientDatabaseRetryPolicy.IsTransientDatabaseFailure"/>).</summary>
internal sealed class DatabaseFailures : IDatabaseFailures
{
    public bool IsTransientDatabaseFailure(Exception exception) => TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(exception);
}
