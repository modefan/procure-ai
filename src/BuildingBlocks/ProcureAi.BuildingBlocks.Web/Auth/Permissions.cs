namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>The permissions of the whole system (spec 4.2). Code checks permissions, never role names.</summary>
public static class Permissions
{
    public const string RequestsRead = "requests.read";
    public const string RequestsCreate = "requests.create";
    public const string RequestsApprove = "requests.approve";
    public const string OrdersRead = "orders.read";
    public const string OrdersReceive = "orders.receive";
    public const string InvoicesRead = "invoices.read";
    public const string InvoicesCreate = "invoices.create";
    public const string InvoicesReleasePayment = "invoices.release-payment";
    public const string BudgetsRead = "budgets.read";
    public const string BudgetsManage = "budgets.manage";
    public const string AssistantUse = "assistant.use";
    public const string AdminAuthorization = "admin.authorization";
    public const string AdminUsers = "admin.users";
    public const string AdminPolicies = "admin.policies";
    public const string AdminSystem = "admin.system";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestsRead, RequestsCreate, RequestsApprove, OrdersRead, OrdersReceive, InvoicesRead, InvoicesCreate,
        InvoicesReleasePayment, BudgetsRead, BudgetsManage, AssistantUse, AdminAuthorization, AdminUsers,
        AdminPolicies, AdminSystem,
    };
}
