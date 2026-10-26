using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ProcureAi.Budgets.Application;

/// <summary>Configuration section <c>Budgets</c>.</summary>
public sealed class BudgetsOptions : IValidatableObject
{
    public const string SectionName = "Budgets";

    /// <summary>Time zone in which the fiscal year starts (1 March, local time). An IANA id such as Africa/Johannesburg.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// How a handler that lost a race in the database (a concurrent reservation on the same budget, or a reserve and a release
    /// of one request) is retried. Set in appsettings (<c>Budgets:ConcurrencyRetry</c>).
    /// </summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
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

/// <summary>
/// Retries with exponential backoff and jitter. A message that lost a race waits inline and runs again, and Wolverine adds a
/// fresh random jitter to the wait at every failure, so contenders that failed together do not come back together. After the
/// last attempt the message is dead-lettered.
/// </summary>
public sealed class ConcurrencyRetryOptions : IValidatableObject
{
    public const int MaxAttempts = 50;

    /// <summary>How many times a message is retried.</summary>
    [Range(1, MaxAttempts)]
    public int Attempts { get; set; }

    /// <summary>The wait before the first retry.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan InitialDelay { get; set; }

    /// <summary>Each wait is this many times the one before, up to <see cref="MaxDelay"/>.</summary>
    [Range(1.0, 10.0)]
    public double Multiplier { get; set; }

    /// <summary>The longest base wait before a retry (the jitter is added on top).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan MaxDelay { get; set; }

    /// <summary>Wolverine adds up to this share of the wait, chosen at random at each failure (0.5: a wait of d becomes d to 1.5 d).</summary>
    [Range(0.0, 1.0)]
    public double JitterFraction { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxDelay < InitialDelay)
        {
            yield return new ValidationResult("MaxDelay cannot be shorter than InitialDelay.", [nameof(MaxDelay)]);
        }
    }

    /// <summary>Throws a clear error when the values are not valid; for code that uses them before the options validation runs.</summary>
    public void EnsureValid()
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true))
        {
            throw new InvalidOperationException(
                $"'{BudgetsOptions.SectionName}:ConcurrencyRetry' is not valid: {string.Join("; ", results.Select(r => r.ErrorMessage))}");
        }
    }

    /// <summary>The base waits before retry 1, 2, ... <see cref="Attempts"/>: exponential, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan[] Delays()
    {
        EnsureValid();
        return [.. Enumerable.Range(0, Attempts).Select(i =>
            TimeSpan.FromMilliseconds(Math.Min(InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, i), MaxDelay.TotalMilliseconds)))];
    }
}
