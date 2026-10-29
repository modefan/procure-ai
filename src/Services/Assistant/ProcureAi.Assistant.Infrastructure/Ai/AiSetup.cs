using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The model providers and their pipelines: the only place that names a provider SDK (an architecture test enforces it). Everything
/// else sees <see cref="IChatClient"/> (keyed <see cref="AiClients.Answer"/> and <see cref="AiClients.Utility"/>) and one
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, so switching provider is configuration (<c>AI:Provider</c>).
/// </summary>
public static class AiSetup
{
    /// <summary>
    /// Registers the validated <c>AI</c> and <c>Assistant</c> options, the metrics and the clients. Outermost first:
    /// <list type="bullet">
    /// <item><c>answer</c>: <see cref="IAnswerGuardrail"/> (when registered, Task 4) → default limits (<c>AI:Chat:MaxOutputTokens</c>) →
    /// function invocation (at most <c>Assistant:Tools:MaxCallsPerQuestion</c> rounds) → OpenTelemetry (<see cref="AiClients.TelemetrySource"/>;
    /// prompt and answer text only with <c>AI:Telemetry:LogContent</c>) → logging → provider;</item>
    /// <item><c>utility</c>: default limits → OpenTelemetry → logging → provider;</item>
    /// <item>the embedding generator: OpenTelemetry → logging → provider, at <c>AI:Embedding:Dimensions</c> (1536).</item>
    /// </list>
    /// The pipelines are built when first resolved, from the options (validated at startup).
    /// </summary>
    public static IServiceCollection AddAssistantAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<AiOptions>(configuration, AiOptions.SectionName);
        services.AddValidatedOptions<AssistantOptions>(configuration, AssistantOptions.SectionName);
        services.AddMetrics();
        services.AddSingleton<AssistantMetrics>();

        // One provider client per process (the OpenAI client is thread-safe and pools its connections).
        services.AddSingleton(sp => ProviderClients.Create(Ai(sp)));

        services.AddKeyedChatClient(AiClients.Answer, sp => sp.GetRequiredService<ProviderClients>().Chat)
            .Use((inner, sp) => sp.GetService<IAnswerGuardrail>()?.Wrap(inner) ?? inner)
            .Use((inner, sp) => WithDefaultLimits(inner, Ai(sp)))
            .Use((inner, sp) => new FunctionInvokingChatClient(inner, sp.GetService<ILoggerFactory>(), sp)
            {
                MaximumIterationsPerRequest = sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Tools.MaxCallsPerQuestion,
            })
            .Use(Traced)
            .Use(Logged);

        services.AddKeyedChatClient(AiClients.Utility, sp => sp.GetRequiredService<ProviderClients>().Chat)
            .Use((inner, sp) => WithDefaultLimits(inner, Ai(sp)))
            .Use(Traced)
            .Use(Logged);

        services.AddEmbeddingGenerator(sp => sp.GetRequiredService<ProviderClients>().Embeddings)
            .Use((inner, sp) => new OpenTelemetryEmbeddingGenerator<string, Embedding<float>>(
                inner, sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(OpenTelemetryEmbeddingGenerator<string, Embedding<float>>)), AiClients.TelemetrySource))
            .Use((inner, sp) => new LoggingEmbeddingGenerator<string, Embedding<float>>(
                inner, LoggerFor<LoggingEmbeddingGenerator<string, Embedding<float>>>(sp)));

        // The spans of every model call (its meter of the same name is exported with every ProcureAi.* meter).
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(AiClients.TelemetrySource));
        return services;
    }

    private static AiOptions Ai(IServiceProvider services) => services.GetRequiredService<IOptions<AiOptions>>().Value;

    private static IChatClient WithDefaultLimits(IChatClient inner, AiOptions ai) =>
        new ConfigureOptionsChatClient(inner, options => options.MaxOutputTokens ??= ai.Chat.MaxOutputTokens);

    private static IChatClient Traced(IChatClient inner, IServiceProvider services) =>
        new OpenTelemetryChatClient(inner, LoggerFor<OpenTelemetryChatClient>(services), AiClients.TelemetrySource)
        {
            // Prompt and answer text in spans only where allowed (true only in appsettings.Development.json).
            EnableSensitiveData = Ai(services).Telemetry.LogContent,
        };

    // LoggingChatClient writes message content only at Trace level, which no shipped Serilog configuration enables.
    private static IChatClient Logged(IChatClient inner, IServiceProvider services) =>
        new LoggingChatClient(inner, LoggerFor<LoggingChatClient>(services));

    private static ILogger LoggerFor<T>(IServiceProvider services) =>
        (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}

/// <summary>The provider's chat client and embedding generator, built once from <c>AI</c> (<see cref="AiOptions"/>).</summary>
internal sealed class ProviderClients(IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
    public IChatClient Chat { get; } = chat;

    public IEmbeddingGenerator<string, Embedding<float>> Embeddings { get; } = embeddings;

    public static ProviderClients Create(AiOptions ai)
    {
        switch (ai.Provider)
        {
            case AiProvider.Fake:
                return new ProviderClients(new FakeChatClient(ai.Fake.StreamDelayMs), new FakeEmbeddingGenerator());

            case AiProvider.OpenAI:
                // Any OpenAI-compatible endpoint: Azure Foundry's /openai/v1 or OpenAI. The key never leaves this object.
                var client = new OpenAIClient(
                    new ApiKeyCredential(ai.ApiKey!),
                    new OpenAIClientOptions { Endpoint = new Uri(ai.Endpoint!), NetworkTimeout = ai.Chat.Timeout });
                return new ProviderClients(
                    client.GetChatClient(ai.Chat.Model).AsIChatClient(),
                    client.GetEmbeddingClient(ai.Embedding.Model).AsIEmbeddingGenerator(ai.Embedding.Dimensions));

            default:
                throw new InvalidOperationException($"'{AiOptions.SectionName}:{nameof(AiOptions.Provider)}' is not set to a known provider.");
        }
    }
}
