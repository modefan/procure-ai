using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ingestion;

namespace ProcureAi.Assistant.Application;

/// <summary>The <c>Assistant</c> section: the service's own limits (plan 05 rulings 7, 8 and 12). Later tasks add retrieval.</summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    [Required, ValidateObjectMembers]
    public ToolOptions Tools { get; set; } = new();

    [Required, ValidateObjectMembers]
    public QuotaOptions Quota { get; set; } = new();

    /// <summary>How documents are cut into chunks (<c>Assistant:Chunking</c>).</summary>
    [Required, ValidateObjectMembers]
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>How documents are embedded (<c>Assistant:Ingestion</c>).</summary>
    [Required, ValidateObjectMembers]
    public IngestionOptions Ingestion { get; set; } = new();

    /// <summary>What may be uploaded (<c>Assistant:Uploads</c>).</summary>
    [Required, ValidateObjectMembers]
    public UploadOptions Uploads { get; set; } = new();
}

/// <summary>The ingestion job of a policy document version (<see cref="Ingestion.IngestPolicyDocument"/>).</summary>
public sealed class IngestionOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Ingestion";

    /// <summary>How many chunks one embedding call carries (and how many are stored per commit).</summary>
    [Range(1, 256)]
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Whether each new chunk gets one or two sentences of context from the <c>utility</c> model before it is embedded (contextual
    /// retrieval). False saves a model call per chunk.
    /// </summary>
    public bool ContextualRetrieval { get; set; } = true;

    /// <summary>
    /// The waits before the retries of a version whose model calls failed (provider unreachable, 429/5xx, timeout), comma separated
    /// (<c>00:00:10, 00:01:00, 00:05:00</c>); after the last retry the version is <c>Failed</c> with the reason. One string, not an array:
    /// an array in configuration is appended to the default in code instead of replacing it.
    /// </summary>
    [Required]
    public string RetryDelays { get; set; } = "00:00:10, 00:01:00, 00:05:00";

    /// <summary>The parsed <see cref="RetryDelays"/>; empty when they are not valid (startup validation reports that).</summary>
    public TimeSpan[] RetryDelaySchedule() =>
        TryParseDelays(RetryDelays, out var delays) ? delays : [];

    /// <summary>How many documents one instance ingests at the same time.</summary>
    [Range(1, 16)]
    public int MaxParallelDocuments { get; set; } = 2;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TryParseDelays(RetryDelays, out _))
        {
            yield return new ValidationResult(
                $"'{SectionPath}:{nameof(RetryDelays)}' must be 1 to 10 comma-separated times (hh:mm:ss), each more than 0 and at most 1 hour.",
                [nameof(RetryDelays)]);
        }
    }

    private static bool TryParseDelays(string? text, out TimeSpan[] delays)
    {
        delays = [];
        var parts = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<TimeSpan>();
        foreach (var part in parts)
        {
            if (!TimeSpan.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var delay)
                || delay <= TimeSpan.Zero || delay > TimeSpan.FromHours(1))
            {
                return false;
            }

            parsed.Add(delay);
        }

        if (parsed.Count is < 1 or > 10)
        {
            return false;
        }

        delays = [.. parsed];
        return true;
    }
}

/// <summary>The upload limits of policy documents (ruling 12).</summary>
public sealed class UploadOptions
{
    /// <summary>The largest file accepted (10 MB); a larger one is answered 413.</summary>
    [Range(1, 100L * 1024 * 1024)]
    public long MaxBytes { get; set; } = 10L * 1024 * 1024;
}

public sealed class ToolOptions
{
    /// <summary>The most tool calls one question may make; then the model answers with what it has (ruling 8).</summary>
    [Range(1, 20)]
    public int MaxCallsPerQuestion { get; set; } = 5;
}

public sealed class QuotaOptions
{
    /// <summary>Questions per user per South African calendar day (ruling 7).</summary>
    [Range(1, 10_000)]
    public int QuestionsPerDay { get; set; } = 50;
}
