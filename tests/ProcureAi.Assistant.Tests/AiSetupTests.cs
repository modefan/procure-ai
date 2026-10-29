using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>The <c>AI</c> options are checked at startup, and the clients are the configured pipelines over the configured provider.</summary>
public sealed class AiSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Shipped = new()
    {
        ["AI:Provider"] = "Fake",
        ["AI:Chat:Model"] = "gpt-4.1-mini",
        ["AI:Chat:MaxOutputTokens"] = "800",
        ["AI:Embedding:Model"] = "text-embedding-3-small",
        ["AI:Embedding:Dimensions"] = "1536",
        ["AI:Fake:StreamDelayMs"] = "0",
        ["Assistant:Tools:MaxCallsPerQuestion"] = "5",
        ["Assistant:Quota:QuestionsPerDay"] = "50",
    };

    private static ServiceProvider Build(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>(Shipped);
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>What the host does at start (ValidateOnStart): every validated options type is checked.</summary>
    private static void Start(ServiceProvider provider) => provider.GetRequiredService<IStartupValidator>().Validate();

    [Fact]
    public void Shipped_fake_settings_start()
    {
        using var provider = Build();

        Should.NotThrow(() => Start(provider));
        provider.GetRequiredService<IOptions<AiOptions>>().Value.EffectiveChatModel.ShouldBe(AiOptions.FakeChatModel);
    }

    [Fact]
    public void OpenAI_provider_requires_endpoint_and_key()
    {
        using var neither = Build(("AI:Provider", "OpenAI"));
        using var badEndpoint = Build(("AI:Provider", "OpenAI"), ("AI:Endpoint", "not a url"), ("AI:ApiKey", "k"));
        using var both = Build(("AI:Provider", "OpenAI"), ("AI:Endpoint", "https://example.openai.azure.com/openai/v1/"), ("AI:ApiKey", "test-key"));

        var missing = Should.Throw<OptionsValidationException>(() => Start(neither));
        missing.Message.ShouldContain("AI:Endpoint");
        missing.Message.ShouldContain("AI:ApiKey");
        Should.Throw<OptionsValidationException>(() => Start(badEndpoint)).Message.ShouldContain("AI:Endpoint");
        Should.NotThrow(() => Start(both));
        // No call is made: the client is only built.
        both.GetRequiredKeyedService<IChatClient>(AiClients.Answer).GetService<FakeChatClient>().ShouldBeNull("a real provider, not the fake");
    }

    [Theory]
    [InlineData("3072")]
    [InlineData("768")]
    public void Embedding_dimensions_other_than_1536_fail_start(string dimensions)
    {
        using var provider = Build(("AI:Embedding:Dimensions", dimensions));

        Should.Throw<OptionsValidationException>(() => Start(provider)).Message.ShouldContain("1536");
    }

    [Fact]
    public void Missing_provider_fails_start()
    {
        using var provider = Build(("AI:Provider", null));

        Should.Throw<OptionsValidationException>(() => Start(provider)).Message.ShouldContain("Provider");
    }

    [Fact]
    public void Answer_pipeline_has_function_invocation_telemetry_and_logging_over_the_provider_and_utility_has_no_tools()
    {
        using var provider = Build(("Assistant:Tools:MaxCallsPerQuestion", "3"));

        var answer = provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer);
        var utility = provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility);

        answer.GetService<FunctionInvokingChatClient>().ShouldNotBeNull().MaximumIterationsPerRequest.ShouldBe(3);
        answer.GetService<OpenTelemetryChatClient>().ShouldNotBeNull().EnableSensitiveData.ShouldBeFalse();
        answer.GetService<LoggingChatClient>().ShouldBeNull("the message log only with AI:Telemetry:LogContent");
        answer.GetService<FakeChatClient>().ShouldNotBeNull();
        utility.GetService<FunctionInvokingChatClient>().ShouldBeNull("internal prompts never call tools");
        utility.GetService<OpenTelemetryChatClient>().ShouldNotBeNull();
        utility.GetService<FakeChatClient>().ShouldNotBeNull();

        var embeddings = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        embeddings.GetService<FakeEmbeddingGenerator>().ShouldNotBeNull();
        embeddings.GetService<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Answer_client_runs_the_tool_the_model_asks_for_and_answers_with_its_result()
    {
        using var provider = Build();
        var answer = provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer);
        var calls = 0;
        var tool = AIFunctionFactory.Create(
            (string number) =>
            {
                calls++;
                return $"{number} is approved.";
            },
            FakeIntentRules.RequestStatus);

        var response = await answer.GetResponseAsync(
            [new(ChatRole.System, AiTasks.Line(AiTasks.Answer)), new(ChatRole.User, "Where is PR-1043?")],
            new ChatOptions { Tools = [tool] },
            Ct);

        calls.ShouldBe(1);
        response.Text.ShouldBe("From get_request_status: PR-1043 is approved.");
    }

    [Fact]
    public async Task Default_output_limit_comes_from_configuration_unless_the_caller_sets_one_and_the_guardrail_is_outermost()
    {
        var guardSaw = new List<int?>();
        var providerSaw = new List<int?>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(new ConfigurationBuilder().AddInMemoryCollection(Shipped).Build());
        services.AddSingleton<IAnswerGuardrail>(new RecordingGuardrail(guardSaw));
        // The provider replaced by one that records what reaches it (the last registration wins).
        services.AddSingleton(new ProviderClients(new RecordingProvider(providerSaw), new FakeEmbeddingGenerator()));
        await using var provider = services.BuildServiceProvider();
        var answer = provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer);

        await answer.GetResponseAsync([new(ChatRole.User, "hello")], cancellationToken: Ct);
        await answer.GetResponseAsync([new(ChatRole.User, "hello")], new ChatOptions { MaxOutputTokens = 50 }, Ct);

        // The guardrail is the outermost stage: it sees the caller's options, before the default is applied below it.
        answer.ShouldBeOfType<RecordingGuardrail.Stage>();
        guardSaw.ShouldBe([null, 50]);
        providerSaw.ShouldBe([800, 50]);
    }

    private sealed class RecordingProvider(List<int?> seen) : DelegatingChatClient(new FakeChatClient())
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            seen.Add(options?.MaxOutputTokens);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <summary>A stand-in for Task 4's guard: records the options it is called with, then passes the call on.</summary>
    private sealed class RecordingGuardrail(List<int?> seen) : IAnswerGuardrail
    {
        public IChatClient Wrap(IChatClient inner) => new Stage(inner, seen);

        public sealed class Stage(IChatClient inner, List<int?> seen) : DelegatingChatClient(inner)
        {
            public override Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                seen.Add(options?.MaxOutputTokens);
                return base.GetResponseAsync(messages, options, cancellationToken);
            }
        }
    }
}
