using ProcureAi.Assistant.Application.Scanning;
using ProcureAi.Assistant.Domain;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Scanning;

/// <summary>The checks of a scanned invoice (plan 05 Task 6): the model's numbers are checked, never trusted.</summary>
public sealed class InvoiceValidatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);
    private static readonly ScanOptions Options = new();

    private static ScannedInvoice Invoice(
        IEnumerable<ScannedLine>? lines = null, decimal exclVat = 3_750m, decimal vat = 562.50m, decimal inclVat = 4_312.50m, string? date = "2026-10-20") =>
        new(
            "Contoso Hardware (Pty) Ltd", "4123456789", "INV-1", date, "PO-1001",
            [.. lines ?? [new ScannedLine("USB-C Dock", 2, 1_200m, 2_400m), new ScannedLine("Laptop Stand", 3, 450m, 1_350m)]],
            vat, exclVat, inclVat);

    private static ScanCheck Check(ScannedInvoice invoice) => InvoiceValidator.Validate(invoice, Today, Options);

    [Fact]
    public void Consistent_invoice_has_no_warnings_and_high_confidence()
    {
        var result = Check(Invoice());

        result.Warnings.ShouldBeEmpty();
        result.Confidence.ShouldBe(ScanConfidence.High);
    }

    [Fact]
    public void Line_total_is_quantity_times_price_within_one_cent()
    {
        Check(Invoice(lines: [new ScannedLine("A", 3, 33.33m, 99.98m)], exclVat: 99.98m, vat: 15m, inclVat: 114.98m)).Warnings
            .ShouldBeEmpty("99,99 against 99,98 is one cent off");

        var wrong = Check(Invoice(lines: [new ScannedLine("A", 3, 33.33m, 100.50m)], exclVat: 100.50m, vat: 15.08m, inclVat: 115.58m)).Warnings.ShouldHaveSingleItem();
        wrong.Code.ShouldBe(ScanWarningCodes.LineTotal);
        wrong.Message.ShouldContain("R 99,99", customMessage: "the recalculated value is in the warning");
        wrong.Message.ShouldContain("R 100,50");
    }

    [Fact]
    public void Lines_must_add_up_to_the_total_excluding_VAT()
    {
        var warning = Check(Invoice(exclVat: 3_800m, vat: 570m, inclVat: 4_370m)).Warnings.ShouldHaveSingleItem();

        warning.Code.ShouldBe(ScanWarningCodes.LinesSum);
        warning.Message.ShouldContain("R 3\u00A0750,00");
    }

    [Fact]
    public void Vat_is_fifteen_percent_within_fifty_cents()
    {
        Check(Invoice(vat: 563m, inclVat: 4_313m)).Warnings.ShouldBeEmpty("R 0,50 off is allowed");

        var warning = Check(Invoice(vat: 600m, inclVat: 4_350m)).Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe(ScanWarningCodes.Vat);
        warning.Message.ShouldContain("R 562,50");
    }

    [Fact]
    public void Total_including_VAT_is_the_total_plus_VAT()
    {
        var warning = Check(Invoice(inclVat: 4_912.50m)).Warnings.ShouldHaveSingleItem();

        warning.Code.ShouldBe(ScanWarningCodes.Total);
        warning.Message.ShouldContain("R 4\u00A0312,50");
        warning.Message.ShouldContain("R 4\u00A0912,50");
        Check(Invoice(inclVat: 4_912.50m)).Confidence.ShouldBe(ScanConfidence.Medium);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2.5)]
    [InlineData(10_001)]
    public void Quantities_are_whole_numbers_from_1_to_10000(double quantity)
    {
        var q = (decimal)quantity;
        var line = new ScannedLine("A", q, 1m, q);
        var warnings = Check(Invoice(lines: [line], exclVat: q, vat: q * 0.15m, inclVat: q * 1.15m)).Warnings;

        warnings.ShouldContain(w => w.Code == ScanWarningCodes.Quantity);
    }

    [Fact]
    public void Date_in_the_future_is_warned_and_unreadable_date_too()
    {
        Check(Invoice(date: "2026-10-30")).Warnings.ShouldHaveSingleItem().Code.ShouldBe(ScanWarningCodes.FutureDate);
        Check(Invoice(date: "2026-10-29")).Warnings.ShouldBeEmpty("today is fine");
        Check(Invoice(date: null)).Warnings.ShouldBeEmpty("a missing date is not a contradiction");
        Check(Invoice(date: "last Tuesday")).Warnings.ShouldHaveSingleItem().Code.ShouldBe(ScanWarningCodes.Date);
    }

    [Fact]
    public void Confidence_is_medium_with_one_or_two_warnings_and_low_with_more_or_no_lines()
    {
        Check(Invoice(inclVat: 1m)).Confidence.ShouldBe(ScanConfidence.Medium);
        Check(Invoice(inclVat: 1m, vat: 1m)).Confidence.ShouldBe(ScanConfidence.Medium);
        var three = Check(Invoice(inclVat: 1m, vat: 1m, exclVat: 1m));
        three.Warnings.Count.ShouldBeGreaterThan(2);
        three.Confidence.ShouldBe(ScanConfidence.Low);

        var none = Check(Invoice(lines: [], exclVat: 0, vat: 0, inclVat: 0));
        none.Confidence.ShouldBe(ScanConfidence.Low);
        none.Warnings.ShouldContain(w => w.Code == ScanWarningCodes.NoLines);
    }

    [Theory]
    [InlineData("unit price")]
    [InlineData("line total")]
    [InlineData("totals")]
    [InlineData("quantity")]
    public void Huge_model_numbers_are_a_warning_never_an_overflow(string where)
    {
        var max = decimal.MaxValue;
        var invoice = where switch
        {
            "unit price" => Invoice(lines: [new ScannedLine("A", 10, max, 10m)], exclVat: 10m, vat: 1.5m, inclVat: 11.5m),
            "line total" => Invoice(lines: [new ScannedLine("A", 1, 1m, max), new ScannedLine("B", 1, 1m, max)], exclVat: 2m, vat: 0.3m, inclVat: 2.3m),
            "totals" => Invoice(exclVat: max, vat: max, inclVat: max),
            _ => Invoice(lines: [new ScannedLine("A", max, max, max)], exclVat: max, vat: max, inclVat: max),
        };

        var result = Should.NotThrow(() => Check(invoice));

        result.Warnings.ShouldContain(w => w.Code == ScanWarningCodes.AmountOutOfRange || w.Code == ScanWarningCodes.Quantity);
        result.Confidence.ShouldNotBe(ScanConfidence.High);
    }

    [Fact]
    public void Amount_above_the_believable_maximum_is_out_of_range()
    {
        var big = Options.MaxAmount + 1m;

        var warning = Check(Invoice(lines: [new ScannedLine("A", 1, big, 1m)], exclVat: 3_750m)).Warnings
            .Where(w => w.Code == ScanWarningCodes.AmountOutOfRange).ShouldHaveSingleItem();

        warning.Message.ShouldContain("Line 1");
    }

    [Theory]
    [InlineData("unit price")]
    [InlineData("line total")]
    [InlineData("vat")]
    [InlineData("totals")]
    public void Negative_amounts_are_warned_even_when_they_add_up(string where)
    {
        var invoice = where switch
        {
            // Every figure agrees with the others: only the sign is wrong.
            "unit price" or "line total" => Invoice(
                lines: [new ScannedLine("USB-C Dock", 2, -1_200m, -2_400m), new ScannedLine("Laptop Stand", 3, 450m, 1_350m)],
                exclVat: -1_050m, vat: -157.50m, inclVat: -1_207.50m),
            "vat" => Invoice(vat: -562.50m, inclVat: 3_187.50m),
            _ => Invoice(lines: [new ScannedLine("A", 1, 0m, 0m)], exclVat: -100m, vat: -15m, inclVat: -115m),
        };

        var result = Check(invoice);

        result.Warnings.ShouldContain(w => w.Code == ScanWarningCodes.NegativeAmount);
        result.Confidence.ShouldNotBe(ScanConfidence.High);
    }
}
