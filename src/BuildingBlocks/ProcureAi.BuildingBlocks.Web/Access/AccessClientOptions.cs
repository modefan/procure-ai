using System.ComponentModel.DataAnnotations;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>The <c>AccessClient</c> section: how a service (or the gateway) caches and calls the Access service (retries and timeouts from the base class).</summary>
public sealed class AccessClientOptions : ServiceClientResilienceOptions
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

    /// <summary>
    /// How long an instance trusts the tenant generation it holds in memory before it reads it from Redis again. The backplane
    /// normally updates it at once; this bounds how long an instance that missed a backplane message keeps the old generation.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan GenerationMemoryCacheDuration { get; set; } = TimeSpan.FromSeconds(10);
}
