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
        if (ConcurrencyRetry.MaxDelay < ConcurrencyRetry.InitialDelay)
        {
            yield return new ValidationResult("MaxDelay cannot be shorter than InitialDelay.", [nameof(ConcurrencyRetry)]);
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

/// <summary>
/// Retries with exponential backoff and jitter. Every contender of a burst that lost a race comes back after a different time,
/// so they do not collide again in lockstep, and the last of many parallel reservations still gets through before the
/// attempts run out (the message would otherwise be dead-lettered and Approvals would never get an outcome).
/// </summary>
public sealed class ConcurrencyRetryOptions
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

    /// <summary>The longest wait before a retry (before jitter is added, never exceeded after).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan MaxDelay { get; set; }

    /// <summary>A wait varies by up to this share either way (0.5 is plus or minus 50 percent).</summary>
    [Range(0.0, 1.0)]
    public double JitterFraction { get; set; }

    /// <summary>The waits before retry 1, 2, ... <see cref="Attempts"/>, each with its own random jitter.</summary>
    public TimeSpan[] Delays(Random random)
    {
        var delays = new TimeSpan[Attempts];
        for (var i = 0; i < delays.Length; i++)
        {
            var backoff = Math.Min(InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, i), MaxDelay.TotalMilliseconds);
            var jitter = 1 + JitterFraction * (random.NextDouble() * 2 - 1);
            delays[i] = TimeSpan.FromMilliseconds(Math.Min(backoff * jitter, MaxDelay.TotalMilliseconds));
        }

        return delays;
    }
}
