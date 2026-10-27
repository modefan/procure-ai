using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>
/// A requester may only raise and submit requests on the cost centers Access assigns to them (plan 02 ruling 10). The check
/// sits in the command handlers, so no way into the service can skip it.
/// </summary>
public static class CostCenterOwnership
{
    /// <summary>
    /// Throws <see cref="ForbiddenException"/> (403) unless <paramref name="costCenterCode"/> is one of the caller's cost
    /// centers in <paramref name="tenantId"/>. The admin API key (Claude's testing access) acts for every cost center. The
    /// cost centers do not depend on roles, so Access is asked without them.
    /// </summary>
    public static async Task EnsureAsync(
        ICallerContext caller, IAccessDirectory access, string tenantId, string costCenterCode, CancellationToken cancellationToken)
    {
        if (caller.Kind == CallerKind.AdminApiKey)
        {
            return;
        }

        var effective = await access.GetEffectiveAccessAsync(caller.UserId, [], tenantId, cancellationToken);
        if (!effective.CostCenterCodes.Contains(costCenterCode, StringComparer.Ordinal))
        {
            throw new ForbiddenException($"You do not act for cost center '{costCenterCode}'.");
        }
    }
}
