using System.Globalization;
using System.Text.RegularExpressions;
using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Infrastructure.Ai.Fake;

/// <summary>A tool call the Fake model decided to make.</summary>
public sealed record FakeToolCall(string Name, IDictionary<string, object?> Arguments);

/// <summary>
/// The Fake model's "understanding" (ruling 15): simple intent rules that turn a question into a tool call, but only for a tool the
/// caller offered. The first rule that matches wins, in this order:
/// <list type="number">
/// <item>a request number (<c>PR-1043</c>) → <see cref="RequestStatus"/> <c>{ number }</c>;</item>
/// <item>an amount in Rand and a cost center code (<c>R 150 000 ... IT-200</c>) → <see cref="ApprovalChain"/> <c>{ amountInclVat, costCenter }</c>;</item>
/// <item>"budget" and a cost center code → <see cref="Budget"/> <c>{ costCenter }</c>;</item>
/// <item>"supplier" or "suppliers" and the words after it → <see cref="Supplier"/> <c>{ name }</c>;</item>
/// <item>"similar", "past" or "previous" purchases and the words after it → <see cref="SimilarPurchases"/> <c>{ description }</c>;</item>
/// <item>"search" (or "check", "look up") "the policy for" and the words after it → <see cref="SearchPolicy"/> <c>{ query }</c>.</item>
/// </list>
/// The tool and argument names are those of the Assistant's tools (<see cref="AssistantTools"/>, plan 05 Task 5b).
/// </summary>
public static partial class FakeIntentRules
{
    public const string RequestStatus = AssistantTools.GetRequestStatus;
    public const string ApprovalChain = AssistantTools.ExplainApprovalChain;
    public const string Budget = AssistantTools.GetBudget;
    public const string Supplier = AssistantTools.GetSupplier;
    public const string SimilarPurchases = AssistantTools.FindSimilarPurchases;
    public const string SearchPolicy = AssistantTools.SearchPolicy;

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
            return new FakeToolCall(ApprovalChain, new Dictionary<string, object?> { ["amountInclVat"] = amount, ["costCenter"] = costCenter });
        }

        if (offeredTools.Contains(Budget) && costCenter is not null && question.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeToolCall(Budget, new Dictionary<string, object?> { ["costCenter"] = costCenter });
        }

        if (offeredTools.Contains(Supplier) && SupplierQuery().Match(question) is { Success: true } supplier)
        {
            return new FakeToolCall(Supplier, new Dictionary<string, object?> { ["name"] = supplier.Groups["query"].Value.Trim() });
        }

        if (offeredTools.Contains(SimilarPurchases) && SimilarPurchasesQuery().Match(question) is { Success: true } similar)
        {
            return new FakeToolCall(SimilarPurchases, new Dictionary<string, object?> { ["description"] = similar.Groups["query"].Value.Trim() });
        }

        if (offeredTools.Contains(SearchPolicy) && PolicySearchQuery().Match(question) is { Success: true } policy)
        {
            return new FakeToolCall(SearchPolicy, new Dictionary<string, object?> { ["query"] = policy.Groups["query"].Value.Trim() });
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

    [GeneratedRegex(
        @"\b(?:similar|past|previous|earlier)\s+purchases?\b(?:\s+(?:of|for|like|to|with))?\s+(?<query>[\p{L}\p{N} -]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex SimilarPurchasesQuery();

    [GeneratedRegex(
        @"\b(?:search|check|look\s+up)\s+(?:in\s+)?the\s+polic(?:y|ies)\s+(?:for|on|about)\s+(?<query>[\p{L}\p{N} -]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex PolicySearchQuery();
}
