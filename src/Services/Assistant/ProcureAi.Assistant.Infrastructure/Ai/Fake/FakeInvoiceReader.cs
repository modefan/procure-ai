using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Infrastructure.Ai.Fake;

/// <summary>
/// The Fake model's invoice reader (<c>#task: scan</c>): a <see cref="ScannedInvoice"/> as JSON, read with fixed patterns from the text of
/// the <c>&lt;data kind="invoice"&gt;</c> block, so scans are deterministic and cost nothing. It knows the layout of the demo invoices:
/// <c>Supplier:</c>, <c>VAT number:</c>, <c>Invoice number:</c>, <c>Invoice date:</c>, <c>Order number:</c>, lines like
/// <c>2 x USB-C Dock @ R 1 200,00 = R 2 400,00</c>, <c>Total excl. VAT:</c>, <c>VAT 15%:</c> and <c>Total incl. VAT:</c>. Like a real model
/// it copies what is printed and checks nothing; what it cannot find is null, empty or 0.
/// </summary>
internal static partial class FakeInvoiceReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Read(string prompt)
    {
        var block = InvoiceBlock().Match(prompt);
        var text = Whitespace().Replace(block.Success ? block.Groups["text"].Value : prompt, " ").Trim();

        var lines = Line().Matches(text)
            .Select(m => new ScannedLine(
                m.Groups["description"].Value.Trim(), Amount(m.Groups["quantity"].Value), Amount(m.Groups["price"].Value), Amount(m.Groups["total"].Value)))
            .ToList();
        var invoice = new ScannedInvoice(
            Field(text, "Supplier") ?? "",
            Field(text, "VAT number"),
            Field(text, "Invoice number") ?? "",
            Field(text, "Invoice date"),
            Field(text, "Order number"),
            lines,
            AmountAfter(VatLine(), text),
            AmountAfter(TotalExclLine(), text),
            AmountAfter(TotalInclLine(), text));
        return JsonSerializer.Serialize(invoice, Json);
    }

    /// <summary>The text after <c>label:</c> up to the next known label (or the end).</summary>
    private static string? Field(string text, string label)
    {
        var match = Regex.Match(
            text, $@"(?<![A-Za-z]){Regex.Escape(label)}:\s*(?<value>.+?)\s*(?=(?:Supplier|VAT number|Invoice number|Invoice date|Order number|Items|Total excl\. VAT|Total incl\. VAT|VAT \d+\s*%):|$)",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success && match.Groups["value"].Value.Length > 0 ? match.Groups["value"].Value : null;
    }

    private static decimal AmountAfter(Regex pattern, string text) =>
        pattern.Match(text) is { Success: true } match ? Amount(match.Groups["amount"].Value) : 0m;

    /// <summary><c>1 200,00</c> (en-ZA, a space or no-break space between thousands) as 1200.00.</summary>
    private static decimal Amount(string text)
    {
        var plain = text.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(plain, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private const string AmountPattern = @"\d{1,3}(?:[  ]\d{3})*(?:,\d{1,2})?";

    [GeneratedRegex(@"<data\s+kind=""invoice""[^>]*>(?<text>.*?)</data>", RegexOptions.Singleline, 1000)]
    private static partial Regex InvoiceBlock();

    [GeneratedRegex(@"(?<![\d,])(?<quantity>\d+(?:[.,]\d+)?)\s*x\s+(?<description>.+?)\s+@\s*R\s?(?<price>" + AmountPattern + @")\s*=\s*R\s?(?<total>" + AmountPattern + ")", RegexOptions.None, 1000)]
    private static partial Regex Line();

    [GeneratedRegex(@"Total excl\. VAT:\s*R\s?(?<amount>" + AmountPattern + ")", RegexOptions.None, 1000)]
    private static partial Regex TotalExclLine();

    [GeneratedRegex(@"Total incl\. VAT:\s*R\s?(?<amount>" + AmountPattern + ")", RegexOptions.None, 1000)]
    private static partial Regex TotalInclLine();

    [GeneratedRegex(@"(?<!(?:excl|incl)\.\s)\bVAT(?:\s+\d+\s*%)?:\s*R\s?(?<amount>" + AmountPattern + ")", RegexOptions.None, 1000)]
    private static partial Regex VatLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
