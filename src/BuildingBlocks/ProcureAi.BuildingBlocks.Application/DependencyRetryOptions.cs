using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// How a message handler is retried when a service it calls cannot answer (unreachable, timing out, circuit open): scheduled
/// retries with exponential backoff, so the message waits in durable storage instead of holding a listener, and survives a
/// restart. This sits on top of the HTTP client's own short retries, which cover blips of milliseconds; these cover outages of
/// minutes. After the last attempt the message is dead-lettered. Each service binds it from its own section (e.g.
/// <c>Approvals:DependencyRetry</c>); the Wolverine policy is <c>RetryTransientDependencyFailures</c> (Messaging).
/// </summary>
public sealed class DependencyRetryOptions : IValidatableObject
{
    /// <summary>How many times a message is retried.</summary>
    [Range(1, 20)]
    public int Attempts { get; set; }

    /// <summary>The wait before the first retry.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan InitialDelay { get; set; }

    /// <summary>Each wait is this many times the one before, up to <see cref="MaxDelay"/>.</summary>
    [Range(1.0, 10.0)]
    public double Multiplier { get; set; }

    /// <summary>The longest wait before a retry.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan MaxDelay { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxDelay < InitialDelay)
        {
            yield return new ValidationResult("MaxDelay cannot be shorter than InitialDelay.", [nameof(MaxDelay)]);
        }
    }

    /// <summary>The waits before retry 1, 2, ... <see cref="Attempts"/>: exponential, capped at <see cref="MaxDelay"/>.</summary>
    /// <param name="section">The configuration path of these values, for the message when they are not valid.</param>
    public TimeSpan[] Delays(string section = "DependencyRetry")
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true))
        {
            throw new InvalidOperationException(
                $"'{section}' is not valid: {string.Join("; ", results.Select(r => r.ErrorMessage))}");
        }

        return [.. Enumerable.Range(1, Attempts).Select(Backoff.BaseDelayFor)];
    }

    /// <summary>The same values as a <see cref="BackoffOptions"/> (no jitter here: Wolverine adds none to scheduled retries), which owns the formula.</summary>
    public BackoffOptions Backoff => new() { InitialDelay = InitialDelay, Multiplier = Multiplier, MaxDelay = MaxDelay };
}
