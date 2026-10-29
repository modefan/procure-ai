namespace ProcureAi.Assistant.Application.Tools;

/// <summary>One cost center's budget of one fiscal year, in Rand.</summary>
public sealed record BudgetInfo(string CostCenterCode, string FiscalYear, decimal Amount, decimal Reserved, decimal Spent, decimal Available);

/// <summary>The Budgets service, called as the signed-in user (its permissions, tenant and cost centers).</summary>
public interface IBudgetsLookup
{
    /// <summary><c>GET api/v1/budgets?fiscalYear=FY2027</c>: the budgets the user may see in that year.</summary>
    Task<IReadOnlyList<BudgetInfo>> ListAsync(string fiscalYear, CancellationToken cancellationToken);
}
