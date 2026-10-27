using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// Exponential backoff with jitter, in one place: the wait before attempt <c>n</c> is <see cref="InitialDelay"/> times
/// <see cref="Multiplier"/> to the power <c>n - 1</c>, capped at <see cref="MaxDelay"/>. Used by the retry options of message
/// handlers (<see cref="ConcurrencyRetryOptions"/>, <see cref="DependencyRetryOptions"/>) and by anything else that retries.
/// </summary>
public sealed class BackoffOptions
{
    /// <summary>The wait before the first attempt (attempt 1).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan InitialDelay { get; set; }

    /// <summary>Each wait is this many times the one before, up to <see cref="MaxDelay"/>.</summary>
    [Range(1.0, 10.0)]
    public double Multiplier { get; set; } = 2;

    /// <summary>The longest base wait (jitter is applied on top of the capped wait).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan MaxDelay { get; set; }

    /// <summary>The share of the wait that is randomised, in both directions (0.5: a wait of d becomes d/2 to 1.5 d).</summary>
    [Range(0.0, 1.0)]
    public double JitterFraction { get; set; }

    /// <summary>The wait before attempt <paramref name="attempt"/> (1-based) without jitter: exponential, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan BaseDelayFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return TimeSpan.FromMilliseconds(Math.Min(
            InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1), MaxDelay.TotalMilliseconds));
    }

    /// <summary>The wait before attempt <paramref name="attempt"/> with jitter of up to plus or minus <see cref="JitterFraction"/>, never below zero.</summary>
    public TimeSpan DelayFor(int attempt, Random random)
    {
        var baseDelay = BaseDelayFor(attempt);
        if (JitterFraction <= 0)
        {
            return baseDelay;
        }

        var factor = 1 + (((random.NextDouble() * 2) - 1) * JitterFraction);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseDelay.TotalMilliseconds * factor));
    }
}
