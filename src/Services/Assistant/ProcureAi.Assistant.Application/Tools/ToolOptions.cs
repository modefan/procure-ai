using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Tools;

/// <summary>The <c>Assistant:Tools</c> section: the limits of the model's read-only tools (plan 05 ruling 8, Task 5b).</summary>
public sealed class ToolOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Tools";

    /// <summary>The most tool calls one question may make; then the model answers with what it has (ruling 8).</summary>
    [Range(1, 20)]
    public int MaxCallsPerQuestion { get; set; } = 5;

    /// <summary>
    /// How long one tool call may take, its lookups and retries included; then it returns an error result and the answer goes on. The
    /// clients' own timeouts (<c>Assistant:ToolClients</c>) are shorter, so a slow service is normally reported as unavailable first.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest tool result the model gets, in characters (cut with "…").</summary>
    [Range(200, 10_000)]
    public int MaxResultLength { get; set; } = 1500;

    /// <summary>The time zone whose calendar decides the current fiscal year of <c>get_budget</c> (an IANA id, as Budgets uses).</summary>
    [Required]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>The configured time zone, or null when the id is unknown (startup validation reports that).</summary>
    public TimeZoneInfo? FindTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FindTimeZone() is null)
        {
            yield return new ValidationResult($"'{SectionPath}:{nameof(TimeZone)}' is not a known time zone.", [nameof(TimeZone)]);
        }
    }
}
