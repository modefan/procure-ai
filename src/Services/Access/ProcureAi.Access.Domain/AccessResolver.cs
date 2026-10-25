using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>What one user may do in a tenant right now: permissions, the cost centers they act for and their approval limit.</summary>
public sealed record EffectiveAccess(
    IReadOnlySet<string> Permissions, IReadOnlyList<string> CostCenterCodes, Money ApprovalLimit);

/// <summary>The pure rules that combine the matrix and the organisation into one user's access.</summary>
public static class AccessResolver
{
    /// <summary>
    /// Permissions come from the roles the caller presents (the token is authoritative for the caller's own access).
    /// Cost centers are the assigned ones, those the user manages and those of departments the user heads; finance
    /// approvers get all.
    /// </summary>
    public static EffectiveAccess Resolve(
        PermissionMatrix? matrix,
        TenantUser? user,
        string userId,
        IEnumerable<string> roles,
        IReadOnlyCollection<Department> departments,
        IReadOnlyCollection<CostCenter> costCenters)
    {
        var permissions = matrix?.PermissionsFor(roles) ?? new HashSet<string>();
        if (user is null)
        {
            return new EffectiveAccess(permissions, [], Money.Zero);
        }

        var codes = new HashSet<string>(user.CostCenterCodes, StringComparer.Ordinal);
        var headed = departments.Where(d => d.HeadUserId == userId).Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var costCenter in costCenters)
        {
            if (user.IsFinanceApprover || costCenter.ManagerUserId == userId || headed.Contains(costCenter.DepartmentCode))
            {
                codes.Add(costCenter.Code);
            }
        }

        return new EffectiveAccess(permissions, [.. codes.Order(StringComparer.Ordinal)], user.ApprovalLimit);
    }

    /// <summary>True when the user's stored roles grant <see cref="Permissions.RequestsApprove"/> in this tenant's matrix.</summary>
    public static bool CanApprove(PermissionMatrix? matrix, TenantUser user) =>
        matrix?.PermissionsFor(user.Roles).Contains(Permissions.RequestsApprove) == true;
}
