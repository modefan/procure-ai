using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Options;

/// <summary>The <c>Caching</c> configuration section.</summary>
public sealed class CachingOptions
{
    public const string SectionName = "Caching";

    /// <summary>How long GET responses are micro-cached, in seconds.</summary>
    [Range(1, 3600)]
    public int DefaultGetSeconds { get; set; } = 1;

    /// <summary>How long an identical concurrent GET waits for the first one to finish before it gets 503.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The <c>Retry-After</c> value, in seconds, sent with that 503.</summary>
    [Range(1, 3600)]
    public int RetryAfterSeconds { get; set; } = 1;
}
