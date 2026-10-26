using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// How a message handler that lost a race in the database is retried: exponential backoff and jitter. A message that lost a
/// race waits inline and runs again, and Wolverine adds a fresh random jitter to the wait at every failure, so contenders that
/// failed together do not come back together. After the last attempt the message is dead-lettered. Each service binds it from
/// its own section (e.g. <c>Budgets:ConcurrencyRetry</c>); the Wolverine policy is <c>RetryConcurrencyConflicts</c> (Messaging).
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
    /// <param name="section">The configuration path of these values, for the message (e.g. <c>Budgets:ConcurrencyRetry</c>).</param>
    public void EnsureValid(string section = "ConcurrencyRetry")
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true))
        {
            throw new InvalidOperationException(
                $"'{section}' is not valid: {string.Join("; ", results.Select(r => r.ErrorMessage))}");
        }
    }

    /// <summary>The base waits before retry 1, 2, ... <see cref="Attempts"/>: exponential, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan[] Delays(string section = "ConcurrencyRetry")
    {
        EnsureValid(section);
        return [.. Enumerable.Range(0, Attempts).Select(i =>
            TimeSpan.FromMilliseconds(Math.Min(InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, i), MaxDelay.TotalMilliseconds)))];
    }
}
