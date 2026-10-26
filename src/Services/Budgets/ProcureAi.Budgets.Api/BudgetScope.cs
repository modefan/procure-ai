using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Budgets.Api;

/// <summary>
/// Which cost centers a caller may see budgets of. Callers who manage budgets (finance, admin), the admin API key and other
/// services see all of them; everyone else sees only the cost centers they act for, as Access says.
/// </summary>
public sealed class BudgetScope(ICurrentUser user, IAccessDirectory access)
{
    /// <summary>The codes the caller may see, or null when all.</summary>
    public async Task<IReadOnlySet<string>?> VisibleCostCentersAsync(CancellationToken cancellationToken)
    {
        if (user.IsAdminApiKey || user.IsServiceAccount)
        {
            return null;
        }

        var effective = await access.GetEffectiveAccessAsync(user.UserId, [.. user.Roles], user.TenantId!, cancellationToken);
        return effective.Permissions.Contains(Permissions.BudgetsManage)
            ? null
            : effective.CostCenterCodes.ToHashSet(StringComparer.Ordinal);
    }
}
