using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>The default role to permission matrix of spec 4.2: what every new tenant starts with (the demo seed).</summary>
public static class DefaultPermissionMatrix
{
    private static readonly string[] Basics =
    [
        Permissions.RequestsRead, Permissions.RequestsCreate, Permissions.AssistantUse,
        Permissions.OrdersRead, Permissions.InvoicesRead, Permissions.BudgetsRead,
        Permissions.OrdersReceive, Permissions.InvoicesCreate,
    ];

    private static readonly string[] Approvers = [.. Basics, Permissions.RequestsApprove];

    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Roles { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["requester"] = Basics,
            ["manager"] = Approvers,
            ["department-head"] = Approvers,
            ["finance"] = [.. Approvers, Permissions.InvoicesReleasePayment, Permissions.BudgetsManage],
            ["admin"] =
            [
                Permissions.RequestsRead, Permissions.AssistantUse, Permissions.OrdersRead, Permissions.InvoicesRead,
                Permissions.BudgetsRead, Permissions.BudgetsManage, Permissions.AdminAuthorization,
                Permissions.AdminUsers, Permissions.AdminPolicies, Permissions.AdminSystem,
            ],
        };

    public static PermissionMatrix CreateFor(string tenantId, DateTimeOffset now) => PermissionMatrix.Create(tenantId, Roles, now);
}
