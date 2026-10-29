using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Infrastructure.Ai.Fake;

/// <summary>A tool call the Fake model decided to make.</summary>
public sealed record FakeToolCall(string Name, IDictionary<string, object?> Arguments);

/// <summary>
/// The Fake model's "understanding" (ruling 15): simple intent rules that turn a question into a tool call, but only for a tool the
/// caller offered. The first rule that matches wins, in this order:
/// <list type="number">
/// <item>a request number (<c>PR-1043</c>) → <see cref="RequestStatus"/> <c>{ number }</c>;</item>
/// <item>an amount in Rand and a cost center code (<c>R 150 000 ... IT-200</c>) → <see cref="ApprovalChain"/> <c>{ amount, costCenterCode }</c>;</item>
/// <item>"budget" and a cost center code → <see cref="Budget"/> <c>{ costCenterCode }</c>;</item>
/// <item>"supplier" or "suppliers" and the words after it → <see cref="SearchSuppliers"/> <c>{ query }</c>.</item>
/// </list>
/// The tool names and argument names are the contract with the Assistant's tools (plan 05 Task 5b).
/// </summary>
public static partial class FakeIntentRules
{
    public const string RequestStatus = "get_request_status";
    public const string ApprovalChain = "explain_approval_chain";
    public const string Budget = "get_budget";
    public const string SearchSuppliers = "search_suppliers";

    public static FakeToolCall? Match(string question, IReadOnlySet<string> offeredTools)
    {
        if (string.IsNullOrWhiteSpace(question) || offeredTools.Count == 0)
        {
            return null;
        }

        if (offeredTools.Contains(RequestStatus) && RequestNumber().Match(question) is { Success: true } number)
        {
            return new FakeToolCall(RequestStatus, new Dictionary<string, object?> { ["number"] = number.Value.ToUpperInvariant() });
        }

        var costCenter = CostCenter(question);
        if (offeredTools.Contains(ApprovalChain) && costCenter is not null && Amount(question) is { } amount)
        {
            return new FakeToolCall(ApprovalChain, new Dictionary<string, object?> { ["amount"] = amount, ["costCenterCode"] = costCenter });
        }

        if (offeredTools.Contains(Budget) && costCenter is not null && question.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeToolCall(Budget, new Dictionary<string, object?> { ["costCenterCode"] = costCenter });
        }

        if (offeredTools.Contains(SearchSuppliers) && SupplierQuery().Match(question) is { Success: true } supplier)
        {
            return new FakeToolCall(SearchSuppliers, new Dictionary<string, object?> { ["query"] = supplier.Groups["query"].Value.Trim() });
        }

        return null;
    }

    /// <summary>A cost center code such as <c>IT-200</c> or <c>CC-100</c> (not a request or order number).</summary>
    internal static string? CostCenter(string text) =>
        CostCenterCode().Matches(text).Select(m => m.Value.ToUpperInvariant())
            .FirstOrDefault(code => !code.StartsWith("PR-", StringComparison.Ordinal) && !code.StartsWith("PO-", StringComparison.Ordinal));

    /// <summary>An amount in Rand: <c>R 150 000</c>, <c>R150,000</c>, <c>R 4 200,50</c> (en-ZA) or <c>R4200.50</c>.</summary>
    internal static decimal? Amount(string text)
    {
        var match = RandAmount().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups["value"].Value.Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        // A comma or point followed by exactly two digits at the end is the decimal separator; every other one groups thousands.
        var decimalPart = DecimalTail().Match(digits);
        var whole = decimalPart.Success ? digits[..decimalPart.Index] : digits;
        whole = whole.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        var normalised = decimalPart.Success ? $"{whole}.{decimalPart.Groups["cents"].Value}" : whole;
        return decimal.TryParse(normalised, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    [GeneratedRegex(@"\bPR-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequestNumber();

    [GeneratedRegex(@"\b[A-Za-z]{2,5}-\d{2,5}\b")]
    private static partial Regex CostCenterCode();

    [GeneratedRegex(@"\bR\s?(?<value>\d[\d  ,.]*\d|\d)")]
    private static partial Regex RandAmount();

    [GeneratedRegex(@"[,.](?<cents>\d{2})$")]
    private static partial Regex DecimalTail();

    [GeneratedRegex(@"\bsuppliers?\b(?:\s+(?:for|of|that|who|which|selling|with))?\s+(?<query>[\p{L}\p{N} -]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex SupplierQuery();
}
