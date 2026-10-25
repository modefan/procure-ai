using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>The <c>Idempotency</c> configuration section.</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a stored response is replayed for the same idempotency key.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a duplicate waits for the first call to finish before it gets 503.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The <c>Retry-After</c> value, in seconds, sent with 503.</summary>
    [Range(1, 3600)]
    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>
    /// How long a failure is shared with duplicates that were waiting while the first call ran. A fresh retry
    /// after the failure always runs again.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan FailureShare { get; set; } = TimeSpan.FromSeconds(5);
}
