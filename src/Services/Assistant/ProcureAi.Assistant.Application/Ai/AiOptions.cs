using System.ComponentModel.DataAnnotations;
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

    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>. Required for <see cref="AiProvider.OpenAI"/>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Required for <see cref="AiProvider.OpenAI"/>. Never logged.</summary>
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

    public bool IsFake => Provider == AiProvider.Fake;

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
        if (Provider != AiProvider.OpenAI)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(Endpoint)}' must be an absolute http(s) URL for the {AiProvider.OpenAI} provider.", [nameof(Endpoint)]);
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(ApiKey)}' is required for the {AiProvider.OpenAI} provider (user secrets or AI__ApiKey).", [nameof(ApiKey)]);
        }
    }
}

public sealed class ChatModelOptions
{
    /// <summary>The chat model (or the Foundry deployment name), e.g. <c>gpt-4.1-mini</c>.</summary>
    [Required, MinLength(1)]
    public string Model { get; set; } = "";

    /// <summary>The most an answer may produce (ruling 8).</summary>
    [Range(1, 16_384)]
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>How long one call to the provider may take.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
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
