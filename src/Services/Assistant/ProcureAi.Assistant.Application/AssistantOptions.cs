using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;

namespace ProcureAi.Assistant.Application;

/// <summary>The <c>Assistant</c> section: the service's own limits (plan 05 rulings 7, 8, 9 and 12).</summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    [Required, ValidateObjectMembers]
    public ToolOptions Tools { get; set; } = new();

    [Required, ValidateObjectMembers]
    public QuotaOptions Quota { get; set; } = new();

    /// <summary>How an answer is streamed (<c>Assistant:Answer</c>).</summary>
    [Required, ValidateObjectMembers]
    public AnswerOptions Answer { get; set; } = new();

    /// <summary>How documents are cut into chunks (<c>Assistant:Chunking</c>).</summary>
    [Required, ValidateObjectMembers]
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>How documents are embedded (<c>Assistant:Ingestion</c>).</summary>
    [Required, ValidateObjectMembers]
    public IngestionOptions Ingestion { get; set; } = new();

    /// <summary>What may be uploaded (<c>Assistant:Uploads</c>).</summary>
    [Required, ValidateObjectMembers]
    public UploadOptions Uploads { get; set; } = new();

    /// <summary>How hybrid search finds sources (<c>Assistant:Retrieval</c>).</summary>
    [Required, ValidateObjectMembers]
    public RetrievalOptions Retrieval { get; set; } = new();

    /// <summary>The input and output guards (<c>Assistant:Guard</c>, ruling 11).</summary>
    [Required, ValidateObjectMembers]
    public GuardOptions Guard { get; set; } = new();

    /// <summary>How past purchases are kept and indexed (<c>Assistant:Purchases</c>).</summary>
    [Required, ValidateObjectMembers]
    public PastPurchasesOptions Purchases { get; set; } = new();
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

    /// <summary>Parses 1 to 10 comma-separated waits (<c>hh:mm:ss</c>), each more than 0 and at most 1 hour.</summary>
    internal static bool TryParseDelays(string? text, out TimeSpan[] delays)
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

public sealed class QuotaOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Quota";

    /// <summary>Questions per user per South African calendar day (ruling 7).</summary>
    [Range(1, 10_000)]
    public int QuestionsPerDay { get; set; } = 50;

    /// <summary>The time zone whose calendar day the quota counts (an IANA id).</summary>
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

/// <summary>The <c>Assistant:Answer</c> section: one streamed answer (plan 05 Task 5a).</summary>
public sealed class AnswerOptions
{
    /// <summary>
    /// The lease of the per-conversation answer lock (ruling 6). The holder extends it while the answer streams; a process that dies leaves
    /// the lock for at most this long.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan LockLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many earlier messages a follow-up is rewritten with and the answer model sees.</summary>
    [Range(0, 50)]
    public int HistoryMessages { get; set; } = 6;
}
