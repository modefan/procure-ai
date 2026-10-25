using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>The <c>AccessClient</c> section: how a service (or the gateway) caches and calls the Access service.</summary>
public sealed class AccessClientOptions
{
    public const string SectionName = "AccessClient";

    /// <summary>
    /// How long an answer is served from the cache. Services also evict it when <c>AccessChanged</c> arrives, so 5 minutes
    /// is only the worst case; the gateway has no message bus and uses 30 seconds.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the last good answer may be served while Access cannot be reached.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan FailSafeMaxDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>After a failed refresh, the stale answer is served this long before Access is asked again.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan FailSafeThrottleDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long "no such cost center" is remembered (it is never served from fail-safe).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan NotFoundCacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries of a failed call, on top of the first attempt (the resilience pipeline needs at least one).</summary>
    [Range(1, 5)]
    public int RetryCount { get; set; } = 1;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:30")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>One attempt gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The whole call, retries included, gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(8);
}
