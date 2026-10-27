using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>
/// A requester may only raise and submit requests on the cost centers Access assigns to them (plan 02 ruling 10). The check
/// sits in the command handlers, so no way into the service can skip it.
/// </summary>
public static class CostCenterOwnership
{
    /// <summary>
    /// Throws <see cref="ForbiddenException"/> (403) unless <paramref name="costCenterCode"/> is one of the caller's cost
    /// centers (<see cref="ICallerScope"/>). The admin API key (Claude's testing access) and other services act for every cost center.
    /// </summary>
    public static async Task EnsureAsync(ICallerScope scope, string costCenterCode, CancellationToken cancellationToken)
    {
        if (!(await scope.GetAsync(cancellationToken)).AllowsCostCenter(costCenterCode))
        {
            throw new ForbiddenException($"You do not act for cost center '{costCenterCode}'.");
        }
    }
}
