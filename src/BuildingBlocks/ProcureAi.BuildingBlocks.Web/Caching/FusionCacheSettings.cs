using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>The <c>FusionCache</c> configuration section (the Redis connection string is <c>ConnectionStrings:Redis</c>).</summary>
public sealed class FusionCacheSettings
{
    public const string SectionName = "FusionCache";

    /// <summary>
    /// Prefix for every cache key and lock name in Redis. When not configured it is the application name,
    /// so each service has its own keys even when services share a Redis.
    /// </summary>
    [MinLength(1)]
    public string? KeyPrefix { get; set; }

    /// <summary>Memory cache duration for entries that do not set their own.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Added to the Redis TTL of every entry. The Redis cache package truncates the remaining TTL to whole
    /// seconds, so a 1 s entry would get a 0 s TTL and never reach Redis; entries carry their own logical
    /// expiry, so the padding never serves stale data.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan DistributedTtlPadding { get; set; } = TimeSpan.FromSeconds(1);
}
