using System.Globalization;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Application.Scanning;

/// <summary>The outcome of the checks of a scan: its warnings and how far it can be trusted (<see cref="ScanConfidence"/>).</summary>
public sealed record ScanCheck(IReadOnlyList<ScanWarning> Warnings, string Confidence);

/// <summary>
/// Checks what the model read from an invoice (Review Focus 3): the model's numbers are never trusted, each one is recalculated and a
/// contradiction is a warning that names the recalculated value next to the model's.
/// <list type="bullet">
/// <item>each line total is quantity × unit price (±<see cref="ScanOptions.AmountTolerance"/>);</item>
/// <item>the line totals add up to the total excl. VAT (±<see cref="ScanOptions.AmountTolerance"/>);</item>
/// <item>the VAT is 15 % of the total excl. VAT (±<see cref="ScanOptions.VatTolerance"/>);</item>
/// <item>the total incl. VAT is the total excl. VAT plus the VAT (±<see cref="ScanOptions.AmountTolerance"/>);</item>
/// <item>every quantity is a whole number from 1 to <see cref="ScanOptions.MaxQuantity"/>;</item>
/// <item>the invoice date (when there is one) reads as <c>yyyy-MM-dd</c> and is not after today.</item>
/// </list>
/// Each check compares the invoice's own figures, so one wrong number gives one warning. Confidence: <c>high</c> without warnings,
/// <c>medium</c> with one or two, <c>low</c> with more or when no line was read.
/// </summary>
public static class InvoiceValidator
{
    /// <summary>The longest piece of the model's text (an unreadable date) quoted in a warning.</summary>
    private const int MaxQuotedLength = 40;

    private const int MediumMaxWarnings = 2;

    private static readonly CultureInfo SouthAfrica = CultureInfo.GetCultureInfo("en-ZA");

    public static ScanCheck Validate(ScannedInvoice invoice, DateOnly today, ScanOptions options)
    {
        var warnings = new List<ScanWarning>();
        var lines = invoice.Lines ?? [];

        if (lines.Count == 0)
        {
            warnings.Add(new ScanWarning(ScanWarningCodes.NoLines, "No invoice lines could be read. Enter them by hand."));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var (line, number) = (lines[i], i + 1);
            if (line.Quantity != decimal.Truncate(line.Quantity) || line.Quantity < 1 || line.Quantity > options.MaxQuantity)
            {
                warnings.Add(new ScanWarning(
                    ScanWarningCodes.Quantity,
                    $"Line {number}: the quantity {line.Quantity.ToString("0.####", SouthAfrica)} is not a whole number from 1 to "
                    + $"{options.MaxQuantity.ToString("N0", SouthAfrica)}."));
            }

            var recalculated = Cents(line.Quantity * line.UnitPriceExclVat);
            if (Off(recalculated, line.LineTotalExclVat, options.AmountTolerance))
            {
                warnings.Add(new ScanWarning(
                    ScanWarningCodes.LineTotal,
                    $"Line {number}: {line.Quantity.ToString("0.####", SouthAfrica)} × {Rand(line.UnitPriceExclVat)} is {Rand(recalculated)}, "
                    + $"but the invoice says {Rand(line.LineTotalExclVat)}."));
            }
        }

        var sum = lines.Sum(l => l.LineTotalExclVat);
        if (lines.Count > 0 && Off(sum, invoice.TotalExclVat, options.AmountTolerance))
        {
            warnings.Add(new ScanWarning(
                ScanWarningCodes.LinesSum,
                $"The lines add up to {Rand(sum)}, but the total excl. VAT on the invoice is {Rand(invoice.TotalExclVat)}."));
        }

        var vat = Cents(invoice.TotalExclVat * VatRate.Standard);
        if (Off(vat, invoice.Vat, options.VatTolerance))
        {
            warnings.Add(new ScanWarning(
                ScanWarningCodes.Vat,
                $"VAT of {(VatRate.Standard * 100).ToString("0.##", SouthAfrica)} % on {Rand(invoice.TotalExclVat)} is {Rand(vat)}, but the invoice says {Rand(invoice.Vat)}."));
        }

        var total = invoice.TotalExclVat + invoice.Vat;
        if (Off(total, invoice.TotalInclVat, options.AmountTolerance))
        {
            warnings.Add(new ScanWarning(
                ScanWarningCodes.Total,
                $"{Rand(invoice.TotalExclVat)} plus VAT of {Rand(invoice.Vat)} is {Rand(total)}, but the total incl. VAT on the invoice is "
                + $"{Rand(invoice.TotalInclVat)}."));
        }

        if (!string.IsNullOrWhiteSpace(invoice.InvoiceDate))
        {
            if (ParseDate(invoice.InvoiceDate) is not { } date)
            {
                warnings.Add(new ScanWarning(
                    ScanWarningCodes.Date, $"The invoice date \"{Quote(invoice.InvoiceDate)}\" could not be read. Enter it by hand."));
            }
            else if (date > today)
            {
                warnings.Add(new ScanWarning(
                    ScanWarningCodes.FutureDate,
                    $"The invoice date {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} is in the future "
                    + $"(today is {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})."));
            }
        }

        var confidence = lines.Count == 0 || warnings.Count > MediumMaxWarnings ? ScanConfidence.Low
            : warnings.Count == 0 ? ScanConfidence.High
            : ScanConfidence.Medium;
        return new ScanCheck(warnings, confidence);
    }

    /// <summary>The date of a scan's <c>yyyy-MM-dd</c> text, or null when it is not one.</summary>
    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static bool Off(decimal expected, decimal actual, decimal tolerance) => Math.Abs(expected - actual) > tolerance;

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string Rand(decimal amount) => Money.Zar(amount).ToDisplayString();

    private static string Quote(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxQuotedLength ? flat : flat[..MaxQuotedLength] + "…";
    }
}
