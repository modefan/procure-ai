using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Options;

/// <summary>The <c>Caching</c> configuration section.</summary>
public sealed class CachingOptions
{
    public const string SectionName = "Caching";

    /// <summary>How long GET responses are micro-cached, in seconds.</summary>
    [Range(1, 3600)]
    public int DefaultGetSeconds { get; set; } = 1;
}
