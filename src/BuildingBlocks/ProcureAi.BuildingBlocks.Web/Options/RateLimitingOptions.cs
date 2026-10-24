using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Options;

/// <summary>The <c>RateLimiting</c> configuration section (fixed window).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests allowed per window and client.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 100;

    /// <summary>Window length in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 1;

    /// <summary>Requests that may wait for the next window instead of being rejected.</summary>
    [Range(0, 10_000)]
    public int QueueLimit { get; set; }
}
