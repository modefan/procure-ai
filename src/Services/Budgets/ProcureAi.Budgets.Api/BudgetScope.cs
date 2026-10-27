using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Budgets.Api;

/// <summary>
/// Which cost centers a caller may see budgets of. The admin API key and other services see all of them
/// (<see cref="ICallerScope"/>), and so do callers who manage budgets (finance, admin: <c>budgets.manage</c> from the token's
/// roles); everyone else sees only the cost centers they act for, as Access says.
/// </summary>
public sealed class BudgetScope(ICallerScope scope, ICurrentUser user, IAccessDirectory access)
{
    /// <summary>The codes the caller may see, or null when all.</summary>
    public async Task<IReadOnlySet<string>?> VisibleCostCentersAsync(CancellationToken cancellationToken)
    {
        var caller = await scope.GetAsync(cancellationToken);
        if (caller.All)
        {
            return null;
        }

        var effective = await access.GetEffectiveAccessAsync(user.UserId, [.. user.Roles], user.TenantId!, cancellationToken);
        return effective.Permissions.Contains(Permissions.BudgetsManage) ? null : caller.CostCenterCodes;
    }
}
