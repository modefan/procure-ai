using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Guardrails;
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
    /// <item><c>answer</c>: the guardrails (<see cref="ChatClientBuilderExtensions.UseGuardrails"/>: <see cref="GuardrailChatClient"/> through
    /// <see cref="IAnswerGuardrail"/>) → default limits (<c>AI:Chat:MaxOutputTokens</c>) →
    /// function invocation (at most <c>Assistant:Tools:MaxCallsPerQuestion</c> rounds) → OpenTelemetry (<see cref="AiClients.TelemetrySource"/>;
    /// prompt and answer text only with <c>AI:Telemetry:LogContent</c>) → logging (one content-free line per call; the
    /// message log only with LogContent) → provider;</item>
    /// <item><c>utility</c>: default limits → OpenTelemetry → logging → provider;</item>
    /// <item><c>guard</c>: the same stages as <c>utility</c> on the classifier model (<c>AI:Guard:Model</c>; by default the chat model, and
    /// then the same provider client);</item>
    /// <item>the embedding generator: OpenTelemetry → logging → provider, at <c>AI:Embedding:Dimensions</c> (1536).</item>
    /// </list>
    /// The pipelines are built when first resolved, from the options (validated at startup). The prompt guard (<see cref="IPromptShield"/>,
    /// used by the answer guardrail, ingestion and invoice scanning) is <see cref="PromptShieldChain"/>: <see cref="PatternPromptShield"/>,
    /// then <see cref="ModelPromptShield"/> on the <c>guard</c> client when <c>Assistant:Guard:Classifier</c> is <c>Model</c> (ruling 11).
    /// </summary>
    public static IServiceCollection AddAssistantAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<AiOptions>(configuration, AiOptions.SectionName);
        services.AddValidatedOptions<AssistantOptions>(configuration, AssistantOptions.SectionName);
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<AssistantMetrics>();

        // One provider client per process (the OpenAI client is thread-safe and pools its connections).
        services.AddSingleton(sp => ProviderClients.Create(Ai(sp)));

        services.AddKeyedChatClient(AiClients.Answer, sp => sp.GetRequiredService<ProviderClients>().Chat)
            .UseGuardrails()
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

        services.AddKeyedChatClient(AiClients.Guard, sp => sp.GetRequiredService<ProviderClients>().Guard)
            .Use((inner, sp) => WithDefaultLimits(inner, Ai(sp)))
            .Use(Traced)
            .Use(Logged);

        AddGuards(services);

        services.AddEmbeddingGenerator(sp => sp.GetRequiredService<ProviderClients>().Embeddings)
            .Use((inner, sp) => new OpenTelemetryEmbeddingGenerator<string, Embedding<float>>(
                inner, sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(OpenTelemetryEmbeddingGenerator<string, Embedding<float>>)), AiClients.TelemetrySource))
            .Use(LoggedEmbeddings);

        // The spans go to the source AiClients.TelemetrySource, which the service defaults trace with every ProcureAi.* source (and its meter
        // of the same name is exported with every ProcureAi.* meter).
        return services;
    }

    /// <summary>The prompt guard chain (ruling 11) and the answer guardrail. A test may register its own <see cref="IAnswerGuardrail"/>.</summary>
    private static void AddGuards(IServiceCollection services)
    {
        services.AddSingleton(sp => new PatternPromptShield(Guard(sp)));
        services.AddSingleton(sp => new ModelPromptShield(
            sp.GetRequiredKeyedService<IChatClient>(AiClients.Guard), sp.GetRequiredService<ILogger<ModelPromptShield>>()));
        services.AddSingleton<IPromptShield>(sp => new PromptShieldChain(Guard(sp).Classifier switch
        {
            GuardClassifier.Model => [sp.GetRequiredService<PatternPromptShield>(), sp.GetRequiredService<ModelPromptShield>()],
            _ => [sp.GetRequiredService<PatternPromptShield>()],
        }));
        services.TryAddSingleton<IAnswerGuardrail, AnswerGuardrail>();
    }

    private static GuardOptions Guard(IServiceProvider services) => services.GetRequiredService<IOptions<AssistantOptions>>().Value.Guard;

    private static AiOptions Ai(IServiceProvider services) => services.GetRequiredService<IOptions<AiOptions>>().Value;

    private static IChatClient WithDefaultLimits(IChatClient inner, AiOptions ai) =>
        new ConfigureOptionsChatClient(inner, options => options.MaxOutputTokens ??= ai.Chat.MaxOutputTokens);

    private static IChatClient Traced(IChatClient inner, IServiceProvider services) =>
        new OpenTelemetryChatClient(inner, LoggerFor<OpenTelemetryChatClient>(services), AiClients.TelemetrySource)
        {
            // Prompt and answer text in spans only where allowed (true only in appsettings.Development.json).
            EnableSensitiveData = Ai(services).Telemetry.LogContent,
        };

    /// <summary>
    /// The logging stage: one content-free line per call (<see cref="ModelCallLogging"/>). Microsoft.Extensions.AI's
    /// <see cref="LoggingChatClient"/> writes prompts and answers (at Trace level), so it is added below it only when
    /// <c>AI:Telemetry:LogContent</c> is true: the option is the gate, not the log level.
    /// </summary>
    private static IChatClient Logged(IChatClient inner, IServiceProvider services)
    {
        var content = Ai(services).Telemetry.LogContent ? new LoggingChatClient(inner, LoggerFor<LoggingChatClient>(services)) : inner;
        return new ModelCallLoggingChatClient(content, CallLogger(services));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> LoggedEmbeddings(
        IEmbeddingGenerator<string, Embedding<float>> inner, IServiceProvider services)
    {
        var content = Ai(services).Telemetry.LogContent
            ? new LoggingEmbeddingGenerator<string, Embedding<float>>(inner, LoggerFor<LoggingEmbeddingGenerator<string, Embedding<float>>>(services))
            : inner;
        return new ModelCallLoggingEmbeddingGenerator(content, CallLogger(services));
    }

    private static ILogger CallLogger(IServiceProvider services) =>
        (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger(ModelCallLogging.Category) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static ILogger LoggerFor<T>(IServiceProvider services) =>
        (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}

/// <summary>The provider's chat client and embedding generator, built once from <c>AI</c> (<see cref="AiOptions"/>).</summary>
internal sealed class ProviderClients(IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings, IChatClient? guard = null)
{
    public IChatClient Chat { get; } = chat;

    /// <summary>The classifier model of the guard (<see cref="AiOptions.EffectiveGuardModel"/>): <see cref="Chat"/> unless another model is set.</summary>
    public IChatClient Guard { get; } = guard ?? chat;

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
                var chat = client.GetChatClient(ai.Chat.Model).AsIChatClient();
                return new ProviderClients(
                    chat,
                    client.GetEmbeddingClient(ai.Embedding.Model).AsIEmbeddingGenerator(ai.Embedding.Dimensions),
                    ai.EffectiveGuardModel == ai.Chat.Model ? chat : client.GetChatClient(ai.EffectiveGuardModel).AsIChatClient());

            default:
                throw new InvalidOperationException($"'{AiOptions.SectionName}:{nameof(AiOptions.Provider)}' is not set to a known provider.");
        }
    }
}
