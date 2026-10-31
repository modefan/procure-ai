using Shouldly;
using UglyToad.PdfPig;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// The README's sample invoice (<c>docs/samples/invoice-PO-1001.pdf</c>) is Fabrikam's invoice for the seeded PO-1001 (plan 06 ruling 3),
/// the same text <see cref="InvoicePdf.FabrikamPo1001"/> writes. No AppHost needed. Set UPDATE_SAMPLES=1 to write the file again.
/// </summary>
public sealed class SampleInvoiceTests
{
    [Fact]
    public void Readme_sample_invoice_is_fabrikam_po_1001()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "samples", "invoice-PO-1001.pdf");
        var expected = InvoicePdf.FabrikamPo1001();
        if (Environment.GetEnvironmentVariable("UPDATE_SAMPLES") is "1")
        {
            File.WriteAllBytes(path, expected);
        }

        var text = Text(File.ReadAllBytes(path));
        text.ShouldBe(Text(expected));
        text.ShouldContain("Fabrikam Office");
        text.ShouldContain("PO-1001");
        text.ShouldContain("Total incl. VAT: R 6 992,00");
    }

    private static string Text(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProcureAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ProcureAi.slnx not found above " + AppContext.BaseDirectory);
    }
}
