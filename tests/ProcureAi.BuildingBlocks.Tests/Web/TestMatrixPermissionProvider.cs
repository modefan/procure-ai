using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>A fixed role to permission matrix for tests that are not about Access. The admin API key gets every permission.</summary>
public sealed class TestMatrixPermissionProvider : IPermissionProvider
{
    private static readonly string[] Basics =
    [
        Permissions.RequestsRead, Permissions.RequestsCreate, Permissions.AssistantUse,
        Permissions.OrdersRead, Permissions.InvoicesRead, Permissions.BudgetsRead,
        Permissions.OrdersReceive, Permissions.InvoicesCreate,
    ];

    private static readonly string[] Approvers = [.. Basics, Permissions.RequestsApprove];

    private static readonly Dictionary<string, HashSet<string>> Matrix = new(StringComparer.Ordinal)
    {
        ["requester"] = Set(Basics),
        ["manager"] = Set(Approvers),
        ["department-head"] = Set(Approvers),
        ["finance"] = Set([.. Approvers, Permissions.InvoicesReleasePayment, Permissions.BudgetsManage]),
        ["admin"] = Set(
        [
            Permissions.RequestsRead, Permissions.AssistantUse, Permissions.OrdersRead, Permissions.InvoicesRead,
            Permissions.BudgetsRead, Permissions.BudgetsManage, Permissions.AdminAuthorization,
            Permissions.AdminUsers, Permissions.AdminPolicies, Permissions.AdminSystem,
        ]),
    };

    public ValueTask<IReadOnlySet<string>> GetPermissionsAsync(ICurrentUser user, CancellationToken ct)
    {
        if (user.IsAdminApiKey)
        {
            return ValueTask.FromResult(Permissions.All);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in user.Roles)
        {
            if (Matrix.TryGetValue(role, out var permissions))
            {
                result.UnionWith(permissions);
            }
        }

        return ValueTask.FromResult<IReadOnlySet<string>>(result);
    }

    private static HashSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.Ordinal);
}
