namespace ProcureAi.Contracts;

/// <summary>The topic exchanges services publish their integration events to (one per publishing service).</summary>
public static class Exchanges
{
    public const string Requests = "requests";
    public const string Budgets = "budgets";
    public const string Approvals = "approvals";
    public const string Access = "access";
}

/// <summary>Queues that receive commands addressed to one service.</summary>
public static class Queues
{
    public const string BudgetCommands = "budgets-commands";
}
