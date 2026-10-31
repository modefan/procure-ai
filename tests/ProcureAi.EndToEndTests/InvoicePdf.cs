using System.Globalization;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ProcureAi.EndToEndTests;

/// <summary>One line of a test invoice: quantity, description and unit price excl. VAT.</summary>
public sealed record InvoicePdfLine(int Quantity, string Description, decimal UnitPriceExclVat);

/// <summary>
/// Writes a one-page supplier invoice PDF as text lines (Helvetica 11, one line every 20 points), the layout of the scan test files
/// (<c>tests/ProcureAi.Assistant.Tests/Files/invoice-*.pdf</c>, made with this same code). Amounts are South African (<c>R 4 200,00</c>).
/// The scan tests need the order number of an order the user really has: the seeded PO-1001 (see <see cref="FabrikamPo1001"/>). <paramref name="statedTotalInclVat"/> prints another total than the lines add up to (a wrong
/// invoice); <paramref name="extraLine"/> is printed last (an instruction, for a poisoned invoice).
/// </summary>
public static class InvoicePdf
{
    private static readonly CultureInfo Rand = CreateRandCulture();

    public static byte[] Build(
        string supplier, string vatNumber, string invoiceNumber, string invoiceDate, string orderNumber, IReadOnlyList<InvoicePdfLine> lines,
        decimal? statedTotalInclVat = null, string? extraLine = null)
    {
        var exclVat = lines.Sum(l => l.Quantity * l.UnitPriceExclVat);
        var vat = Math.Round(exclVat * 0.15m, 2, MidpointRounding.AwayFromZero);
        List<string> text =
        [
            $"Supplier: {supplier}",
            $"VAT number: {vatNumber}",
            $"Invoice number: {invoiceNumber}",
            $"Invoice date: {invoiceDate}",
            $"Order number: {orderNumber}",
            "Items:",
            .. lines.Select(l => $"{l.Quantity} x {l.Description} @ {Format(l.UnitPriceExclVat)} = {Format(l.Quantity * l.UnitPriceExclVat)}"),
            $"Total excl. VAT: {Format(exclVat)}",
            $"VAT 15%: {Format(vat)}",
            $"Total incl. VAT: {Format(statedTotalInclVat ?? exclVat + vat)}",
        ];
        if (extraLine is not null)
        {
            text.Add(extraLine);
        }

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        var y = 780;
        foreach (var line in text)
        {
            page.AddText(line, 11, new PdfPoint(50, y), font);
            y -= 20;
        }

        return builder.Build();
    }

    /// <summary>
    /// Fabrikam Office's invoice for the seeded PO-1001 (plan 06 ruling 3): two USB-C docks at R 2 350,00 and two laptop stands at R 690,00,
    /// R 6 992,00 incl. VAT. The README's sample <c>docs/samples/invoice-PO-1001.pdf</c> is this with <see cref="SampleInvoiceNumber"/>
    /// (<c>SampleInvoiceTests</c> keeps them the same); the end-to-end scan test prints a fresh invoice number on it.
    /// </summary>
    public static byte[] FabrikamPo1001(string invoiceNumber = SampleInvoiceNumber) =>
        Build("Fabrikam Office (Pty) Ltd", "4234567890", invoiceNumber, "2026-10-30", "PO-1001",
            [new InvoicePdfLine(2, "USB-C Dock", 2_350m), new InvoicePdfLine(2, "Laptop Stand", 690m)]);

    public const string SampleInvoiceNumber = "FAB-20417";

    /// <summary><c>R 4 200,00</c>: a space after the R and between thousands, a decimal comma.</summary>
    public static string Format(decimal amount) => "R " + amount.ToString("#,0.00", Rand);

    private static CultureInfo CreateRandCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }
}
