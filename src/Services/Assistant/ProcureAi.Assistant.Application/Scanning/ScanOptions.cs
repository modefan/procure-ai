using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Scanning;

/// <summary>The <c>Assistant:Scanning</c> section: invoice scanning (plan 05 Task 6).</summary>
public sealed class ScanOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Scanning";

    /// <summary>How many days a scan is kept (a MongoDB TTL index deletes it after that).</summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>How far a line total may be from quantity × unit price, the sum of the lines from the total excl. VAT, and the total incl. VAT from excl. + VAT (R 0,01).</summary>
    [Range(typeof(decimal), "0", "10")]
    public decimal AmountTolerance { get; set; } = 0.01m;

    /// <summary>How far the VAT may be from 15 % of the total excl. VAT (R 0,50: suppliers round per line).</summary>
    [Range(typeof(decimal), "0", "100")]
    public decimal VatTolerance { get; set; } = 0.50m;

    /// <summary>
    /// The largest believable amount on an invoice (a unit price, line total, VAT or total; R 1 000 000 000 by default). A larger figure
    /// from the model is a warning, and is not recalculated (the model's numbers can be as big as a decimal goes).
    /// </summary>
    [Range(typeof(decimal), "1", "1000000000000")]
    public decimal MaxAmount { get; set; } = 1_000_000_000m;

    /// <summary>The largest believable quantity of one line.</summary>
    [Range(1, 1_000_000)]
    public int MaxQuantity { get; set; } = 10_000;

    /// <summary>
    /// The longest invoice text sent to the model, in characters. A longer PDF is refused (422): an invoice is a page or two, and the model
    /// is paid per token.
    /// </summary>
    [Range(1_000, 200_000)]
    public int MaxTextLength { get; set; } = 20_000;

    /// <summary>The most tokens the model may answer with.</summary>
    [Range(100, 16_000)]
    public int MaxOutputTokens { get; set; } = 2_000;

    /// <summary>The time zone whose calendar day decides whether an invoice date is in the future (an IANA id).</summary>
    [Required]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>The configured time zone, or null when the id is unknown (startup validation reports that).</summary>
    public TimeZoneInfo? FindTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FindTimeZone() is null)
        {
            yield return new ValidationResult($"'{SectionPath}:{nameof(TimeZone)}' is not a known time zone.", [nameof(TimeZone)]);
        }
    }
}
