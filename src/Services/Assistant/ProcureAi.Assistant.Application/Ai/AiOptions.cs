using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Ai;

/// <summary>Which model provider answers (ruling 1).</summary>
public enum AiProvider
{
    /// <summary>Deterministic, no key, no account: tests, CI and keyless local runs. Labelled "Demo model" in the UI.</summary>
    Fake,

    /// <summary>Any OpenAI-compatible endpoint: Azure Foundry's <c>/openai/v1</c> or OpenAI itself.</summary>
    OpenAI,

    /// <summary>
    /// Azure AI Foundry through its OpenAI-compatible <c>/openai/v1/</c> endpoint (no <c>api-version</c>), with the deployment names as the
    /// model names. The same client as <see cref="OpenAI"/>; a name of its own so the configuration says what runs.
    /// </summary>
    AzureFoundry,
}

/// <summary>
/// The <c>AI</c> section: provider, endpoint, key and models. Switching provider is configuration only. The key comes from user
/// secrets or the environment (<c>AI__ApiKey</c>), never from a committed file. Invalid settings fail at startup.
/// </summary>
public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "AI";

    /// <summary>The model names the Fake provider reports, so its vectors are never taken for a real model's (ruling 2).</summary>
    public const string FakeChatModel = "fake-chat";

    public const string FakeEmbeddingModel = "fake-embedding-1536";

    [Required]
    public AiProvider? Provider { get; set; }

    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>. Required for an OpenAI-compatible provider.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Required for an OpenAI-compatible provider. Never logged.</summary>
    public string? ApiKey { get; set; }

    [Required, ValidateObjectMembers]
    public ChatModelOptions Chat { get; set; } = new();

    [Required, ValidateObjectMembers]
    public EmbeddingModelOptions Embedding { get; set; } = new();

    [Required, ValidateObjectMembers]
    public GuardModelOptions Guard { get; set; } = new();

    [Required, ValidateObjectMembers]
    public AiTelemetryOptions Telemetry { get; set; } = new();

    [Required, ValidateObjectMembers]
    public PriceOptions Prices { get; set; } = new();

    [Required, ValidateObjectMembers]
    public FakeProviderOptions Fake { get; set; } = new();

    [Required, ValidateObjectMembers]
    public AiResilienceOptions Resilience { get; set; } = new();

    /// <summary>
    /// The most tokens (input plus output of answers and their query rewrites) one tenant may use per calendar day of
    /// <c>Assistant:Quota:TimeZone</c>; once reached, further questions get a friendly 429 until midnight. A cost guard for the live demo.
    /// </summary>
    [Range(1, 1_000_000_000)]
    public long DailyTokenCapPerTenant { get; set; } = 200_000;

    /// <summary>
    /// How many characters of prompt count as one token when a call's tokens are reserved before it is made (the reservation is the prompt
    /// estimate plus the call's output limit; it is settled with the provider's reported usage afterwards, and kept when none arrives).
    /// </summary>
    [Range(1, 16)]
    public int CharsPerTokenEstimate { get; set; } = 4;

    public bool IsFake => Provider == AiProvider.Fake;

    /// <summary>Whether the provider is an OpenAI-compatible endpoint (<see cref="AiProvider.OpenAI"/> or <see cref="AiProvider.AzureFoundry"/>).</summary>
    public bool IsOpenAICompatible => Provider is AiProvider.OpenAI or AiProvider.AzureFoundry;

    /// <summary>The chat model that really answers: the configured one, or <see cref="FakeChatModel"/> under the Fake provider.</summary>
    public string EffectiveChatModel => IsFake ? FakeChatModel : Chat.Model;

    /// <summary>The embedding model stored with every chunk: the configured one, or <see cref="FakeEmbeddingModel"/>.</summary>
    public string EffectiveEmbeddingModel => IsFake ? FakeEmbeddingModel : Embedding.Model;

    /// <summary>The classifier model of the guard: <see cref="GuardModelOptions.Model"/>, else the chat model.</summary>
    public string EffectiveGuardModel => IsFake ? FakeChatModel : string.IsNullOrWhiteSpace(Guard.Model) ? Chat.Model : Guard.Model;

    /// <summary>The prices the cost metric uses: none under the Fake provider (it costs nothing), else <see cref="Prices"/>.</summary>
    public PriceOptions EffectivePrices => IsFake ? new PriceOptions() : Prices;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsOpenAICompatible)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(Endpoint)}' must be an absolute http(s) URL for the {Provider} provider.", [nameof(Endpoint)]);
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(ApiKey)}' is required for the {Provider} provider (user secrets or AI__ApiKey).", [nameof(ApiKey)]);
        }
    }
}

public sealed class ChatModelOptions
{
    /// <summary>The chat model (or the Foundry deployment name), e.g. <c>chat</c>.</summary>
    [Required, MinLength(1)]
    public string Model { get; set; } = "";

    /// <summary>The most an answer may produce (ruling 8).</summary>
    [Range(1, 16_384)]
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>
    /// The least output limit any call gets: a smaller limit a call sets (the classifier's 10, the reranker's 100) is raised to this. 1
    /// changes nothing. A reasoning model counts its thinking against the limit and returns no text at all when a tiny limit is used up
    /// first (gpt-5-mini with 10: empty, <c>finish_reason: length</c>), so set a few hundred for one; only the tokens produced are billed.
    /// </summary>
    [Range(1, 16_384)]
    public int MinOutputTokens { get; set; } = 1;

    /// <summary>How long one call to the provider may take.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The reasoning effort sent with every chat call (answer, utility and guard) that does not set its own; empty sends none (the model's
    /// default). A reasoning model counts its thinking against the output limit, so the short classifier and rewrite calls need a low effort
    /// (<c>None</c>) to leave room for the answer itself.
    /// </summary>
    public ReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// Whether the model accepts a sampling temperature. False for reasoning models (they accept only the default and answer 400 to
    /// <c>temperature: 0</c>): the calls' <c>Temperature</c> and <c>TopP</c> are then left out.
    /// </summary>
    public bool SupportsTemperature { get; set; } = true;
}

public sealed class EmbeddingModelOptions : IValidatableObject
{
    /// <summary>The embedding model, e.g. <c>text-embedding-3-small</c> (1536 natively).</summary>
    [Required, MinLength(1)]
    public string Model { get; set; } = "";

    /// <summary>Fixed at <see cref="Chunk.EmbeddingDimensions"/> (ruling 2): the <c>vector(1536)</c> column. Anything else fails at startup.</summary>
    public int Dimensions { get; set; } = Chunk.EmbeddingDimensions;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Dimensions != Chunk.EmbeddingDimensions)
        {
            yield return new ValidationResult(
                $"'{AiOptions.SectionName}:Embedding:{nameof(Dimensions)}' must be {Chunk.EmbeddingDimensions} (the vector column); it is {Dimensions}.",
                [nameof(Dimensions)]);
        }
    }
}

public sealed class GuardModelOptions
{
    /// <summary>The classifier model of the prompt guard; empty means the chat model.</summary>
    public string? Model { get; set; }
}

public sealed class AiTelemetryOptions
{
    /// <summary>Whether prompt and answer text go into traces and logs. True only in Development; never in a deployment.</summary>
    public bool LogContent { get; set; }
}

/// <summary>Prices in US dollars per million tokens, for the <c>assistant.cost_usd</c> metric (not applied under the Fake provider).</summary>
public sealed class PriceOptions
{
    [Range(typeof(decimal), "0", "1000")]
    public decimal InputPerMillionUsd { get; set; }

    [Range(typeof(decimal), "0", "1000")]
    public decimal OutputPerMillionUsd { get; set; }

    [Range(typeof(decimal), "0", "1000")]
    public decimal EmbeddingPerMillionUsd { get; set; }
}

public sealed class FakeProviderOptions
{
    /// <summary>The pause between streamed words of the Fake chat model, so streaming is visible.</summary>
    [Range(0, 1_000)]
    public int StreamDelayMs { get; set; } = 15;
}

/// <summary>
/// The <c>AI:Resilience</c> section: the circuit breaker in front of every model call (one per pipeline: <c>answer</c>, <c>utility</c>,
/// <c>guard</c> and the embedding generator) and the provider SDK's own retries. When at least <see cref="FailureRatio"/> of at least
/// <see cref="MinimumThroughput"/> calls in <see cref="SamplingDuration"/> fail for a passing reason (timeout, 408, 429, 5xx, no answer),
/// the breaker opens for <see cref="BreakDuration"/>: calls then fail at once, without reaching the provider, through the same
/// "unavailable" paths as a provider failure.
/// </summary>
public sealed class AiResilienceOptions
{
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>The fewest calls in <see cref="SamplingDuration"/> before the breaker may open (Polly needs at least 2).</summary>
    [Range(2, 1_000)]
    public int MinimumThroughput { get; set; } = 4;

    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long an open breaker refuses calls before it lets one through to test the provider.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retries of the provider SDK itself (429 and 5xx, with its own backoff), inside one breaker call. Low, so a provider that is down
    /// is found out quickly; the breaker counts the call once, whatever the retries.
    /// </summary>
    [Range(0, 5)]
    public int ProviderMaxRetries { get; set; } = 1;
}
