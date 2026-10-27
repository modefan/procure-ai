namespace ProcureAi.Contracts;

/// <summary>The topic exchanges services publish their integration events to (one per publishing service).</summary>
public static class Exchanges
{
    public const string Requests = "requests";
    public const string Budgets = "budgets";
    public const string Approvals = "approvals";
    public const string Access = "access";
    public const string Purchasing = "purchasing";
    public const string Invoicing = "invoicing";
}

/// <summary>Queues that receive commands addressed to one service.</summary>
public static class Queues
{
    public const string BudgetCommands = "budgets-commands";
    public const string PurchasingCommands = "purchasing-commands";
}
