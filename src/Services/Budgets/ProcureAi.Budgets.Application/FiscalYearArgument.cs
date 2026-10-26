using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Application;

internal static class FiscalYearArgument
{
    /// <summary>Reads a fiscal year the caller sent (<c>FY2027</c>); anything else is a rule violation.</summary>
    public static FiscalYear Parse(string text) =>
        FiscalYear.TryParse(text, out var year) ? year : throw new DomainException($"'{text}' is not a fiscal year such as FY2027.");
}
