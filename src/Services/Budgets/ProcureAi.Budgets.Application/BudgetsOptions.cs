using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Budgets.Application;

/// <summary>Configuration section <c>Budgets</c>.</summary>
public sealed class BudgetsOptions : IValidatableObject
{
    public const string SectionName = "Budgets";

    /// <summary>Time zone in which the fiscal year starts (1 March, local time). An IANA id such as Africa/Johannesburg.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// How long Wolverine waits before each retry of a handler that lost a race in the database (a concurrent reservation on
    /// the same budget, or a reserve and a release of one request). Set in appsettings (<c>Budgets:ConcurrencyRetryDelays</c>);
    /// the number of delays is the number of retries.
    /// </summary>
    [Required, MinLength(1), MaxLength(MaxRetries)]
    public TimeSpan[] ConcurrencyRetryDelays { get; set; } = [];

    /// <summary>Upper bound on the configured retries, so a typo cannot keep a message in the retry loop for minutes.</summary>
    public const int MaxRetries = 10;

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConcurrencyRetryDelays.Any(delay => delay <= TimeSpan.Zero))
        {
            yield return new ValidationResult("Every retry delay must be positive.", [nameof(ConcurrencyRetryDelays)]);
        }

        if (string.IsNullOrWhiteSpace(TimeZone))
        {
            yield break;
        }

        var known = true;
        try
        {
            _ = Zone;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            known = false;
        }

        if (!known)
        {
            yield return new ValidationResult($"Unknown time zone '{TimeZone}'.", [nameof(TimeZone)]);
        }
    }
}
