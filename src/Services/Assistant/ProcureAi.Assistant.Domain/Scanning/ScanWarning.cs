namespace ProcureAi.Assistant.Domain;

/// <summary>A check of a scan that failed: a <see cref="ScanWarningCodes"/> code and a sentence that names the recalculated value.</summary>
public sealed record ScanWarning(string Code, string Message);

/// <summary>The codes of <see cref="ScanWarning"/>.</summary>
public static class ScanWarningCodes
{
    public const string LineTotal = "line_total";
    public const string LinesSum = "lines_sum";
    public const string Vat = "vat";
    public const string Total = "total";
    public const string Quantity = "quantity";
    public const string FutureDate = "future_date";
    public const string Date = "date";
    public const string NoLines = "no_lines";

    /// <summary>A unit price, line total, VAT or total below zero.</summary>
    public const string NegativeAmount = "negative_amount";

    /// <summary>An amount above <c>Assistant:Scanning:MaxAmount</c>: not a believable invoice figure (and too big to recalculate safely).</summary>
    public const string AmountOutOfRange = "amount_out_of_range";
}

/// <summary>How far the scan can be trusted: <c>high</c> without warnings, <c>medium</c> with one or two, <c>low</c> with more or without lines.</summary>
public static class ScanConfidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}
