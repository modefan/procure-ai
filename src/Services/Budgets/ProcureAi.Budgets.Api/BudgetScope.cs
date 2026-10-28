using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Budgets.Api;

/// <summary>
/// Which cost centers a caller may see budgets of. It is the shared <see cref="ICallerScope"/>: the admin API key and a service
/// acting for nobody see all of them; a service acting on behalf of a user is scoped like that user; everyone else sees the cost
/// centers Access says they act for (finance approvers get every cost center there). A signed-in user whose token grants
/// <c>budgets.manage</c> also sees all, as before.
/// </summary>
public sealed class BudgetScope(ICallerScope scope, ICallerContext caller, ICurrentUser user, IAccessDirectory access)
{
    /// <summary>The codes the caller may see, or null when all.</summary>
    public async Task<IReadOnlySet<string>?> VisibleCostCentersAsync(CancellationToken cancellationToken)
    {
        var callerScope = await scope.GetAsync(cancellationToken);
        if (callerScope.All)
        {
            return null;
        }

        // The token's roles say what a signed-in user may manage; for a service acting for a user there is no such token, so the
        // user's cost centers (all of them for finance) are the whole answer.
        if (caller.Kind is CallerKind.User && caller.TenantId is { } tenant)
        {
            var effective = await access.GetEffectiveAccessAsync(user.UserId, [.. user.Roles], tenant, cancellationToken);
            if (effective.Permissions.Contains(Permissions.BudgetsManage))
            {
                return null;
            }
        }

        return callerScope.CostCenterCodes;
    }
}
