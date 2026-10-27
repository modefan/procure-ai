using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Audit.Application;

/// <summary>The <c>Audit</c> section.</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// How long the history waits for display names from Access (cached; on a miss one call per user, in parallel). When it
    /// runs out or Access fails, the history shows user IDs instead: names are decoration, the history itself must answer.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan DisplayNameTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
