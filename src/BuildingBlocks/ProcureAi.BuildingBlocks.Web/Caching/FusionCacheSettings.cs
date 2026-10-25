using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>The <c>FusionCache</c> configuration section (the Redis connection string is <c>ConnectionStrings:Redis</c>).</summary>
public sealed class FusionCacheSettings
{
    public const string SectionName = "FusionCache";

    /// <summary>Prefix for every cache key in Redis, so services sharing a Redis stay apart.</summary>
    [Required]
    [MinLength(1)]
    public string KeyPrefix { get; set; } = "procure:";
}
