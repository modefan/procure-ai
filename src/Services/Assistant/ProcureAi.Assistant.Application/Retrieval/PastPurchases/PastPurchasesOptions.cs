using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary>The <c>Assistant:Purchases</c> section: the Assistant's copy of purchase requests and their indexing (ruling 9).</summary>
public sealed class PastPurchasesOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Purchases";

    /// <summary>
    /// The waits before the retries of a <c>RequestCompleted</c> whose submit data has not arrived yet, comma separated
    /// (<c>00:00:05, 00:00:30, 00:02:00</c>: three retries). After the last one the completion is kept and the message ends; the purchase
    /// is indexed when <c>RequestSubmitted</c> arrives. One string, not an array (an array in configuration is appended to the default).
    /// </summary>
    [Required]
    public string RetryDelays { get; set; } = "00:00:05, 00:00:30, 00:02:00";

    /// <summary>The time zone of the completion date in the chunk text (an IANA id).</summary>
    [Required]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// Two events of the same request handled at the same time (they arrive in any order): the loser of the row-version race is retried
    /// inline and then sees the winner's data.
    /// </summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new()
    {
        Attempts = 10,
        InitialDelay = TimeSpan.FromMilliseconds(50),
        Multiplier = 2.0,
        MaxDelay = TimeSpan.FromSeconds(2),
        JitterFraction = 0.5,
    };

    /// <summary>The approvers' names could not be looked up (Access unreachable): the message is retried on this schedule.</summary>
    [Required, ValidateObjectMembers]
    public DependencyRetryOptions DependencyRetry { get; set; } = new()
    {
        Attempts = 5,
        InitialDelay = TimeSpan.FromSeconds(5),
        Multiplier = 3.0,
        MaxDelay = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// How long a placeholder (a row whose RequestSubmitted never arrived, e.g. a lost message) is kept before the periodic cleanup
    /// deletes it. A placeholder of a request still being processed is far younger.
    /// </summary>
    [Range(typeof(TimeSpan), "00:10:00", "365.00:00:00")]
    public TimeSpan PlaceholderMaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the cleanup of old placeholders runs.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "7.00:00:00")]
    public TimeSpan PlaceholderCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The parsed <see cref="RetryDelays"/>; empty when they are not valid (startup validation reports that).</summary>
    public TimeSpan[] RetryDelaySchedule() =>
        IngestionOptions.TryParseDelays(RetryDelays, out var delays) ? delays : [];

    /// <summary>The configured time zone, or null when the id is unknown (startup validation reports that).</summary>
    public TimeZoneInfo? FindTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IngestionOptions.TryParseDelays(RetryDelays, out _))
        {
            yield return new ValidationResult(
                $"'{SectionPath}:{nameof(RetryDelays)}' must be 1 to 10 comma-separated times (hh:mm:ss), each more than 0 and at most 1 hour.",
                [nameof(RetryDelays)]);
        }

        if (FindTimeZone() is null)
        {
            yield return new ValidationResult($"'{SectionPath}:{nameof(TimeZone)}' is not a known time zone.", [nameof(TimeZone)]);
        }
    }
}
