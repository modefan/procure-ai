using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Infrastructure.Tools;

/// <summary><see cref="IBudgetsLookup"/> over the Budgets API, as the user (<c>AddProcureAiUserClient</c>).</summary>
public sealed class BudgetsLookup(HttpClient http) : IBudgetsLookup
{
    public const string ServiceName = "budgets-api";

    public async Task<IReadOnlyList<BudgetInfo>> ListAsync(string fiscalYear, CancellationToken cancellationToken)
    {
        var budgets = await ToolHttp.GetAsync<List<BudgetAnswer>>(
            http, $"api/v1/budgets?fiscalYear={ToolHttp.Escape(fiscalYear)}", "Budgets", cancellationToken);
        return [.. budgets.Select(b => new BudgetInfo(b.CostCenterCode, b.FiscalYear, b.Amount, b.Reserved, b.Spent, b.Available))];
    }

    /// <summary>The part of Budgets' <c>BudgetDto</c> the tool shows.</summary>
    private sealed record BudgetAnswer(string CostCenterCode, string FiscalYear, decimal Amount, decimal Reserved, decimal Spent, decimal Available);
}
