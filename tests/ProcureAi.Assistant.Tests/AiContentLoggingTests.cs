using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Infrastructure.Ai;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>
/// Plan 05 Global Constraint: prompt and answer text is never logged unless <c>AI:Telemetry:LogContent</c> is true. The option is the gate,
/// not the log level: even with every logger at Trace, nothing of a prompt or answer reaches a log sink or a span when it is false.
/// </summary>
public sealed class AiContentLoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Shipped = new()
    {
        ["AI:Provider"] = "Fake",
        ["AI:Chat:Model"] = "gpt-4.1-mini",
        ["AI:Embedding:Model"] = "text-embedding-3-small",
        ["AI:Fake:StreamDelayMs"] = "0",
    };

    private sealed record Captured(string Category, LogLevel Level, string Text);

    /// <summary>A log sink that keeps every message and every structured value it is given, at every level.</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<Captured> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<Captured> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
                entries.Enqueue(new Captured(category, logLevel, formatter(state, exception) + " || " + values));
            }
        }
    }

    private static (ServiceProvider Provider, CapturingProvider Logs) Build(bool logContent)
    {
        var settings = new Dictionary<string, string?>(Shipped) { ["AI:Telemetry:LogContent"] = logContent ? "true" : "false" };
        var logs = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAssistantAi(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return (services.BuildServiceProvider(), logs);
    }

    /// <summary>Calls the answer client (plain and streaming), the utility client and the embedding generator with the marker in the text.</summary>
    private static async Task CallEverythingAsync(ServiceProvider provider, string marker)
    {
        // The Fake model answers with the first sentence of a source, so the marker is in the answer too.
        ChatMessage[] messages = [new(ChatRole.System, AiTasks.Line(AiTasks.Answer)), new(ChatRole.User, $"[S1] The {marker} rule applies.\nQuestion {marker}?")];
        var answer = provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer);
        (await answer.GetResponseAsync(messages, cancellationToken: Ct)).Text.ShouldContain(marker);
        await foreach (var _ in answer.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
        }

        await provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility)
            .GetResponseAsync([new(ChatRole.System, AiTasks.Line(AiTasks.Rewrite)), new(ChatRole.User, $"rewrite {marker}")], cancellationToken: Ct);
        await provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().GenerateAsync([$"embed {marker}"], cancellationToken: Ct);
    }

    private static List<Activity> Listen(out ActivityListener listener)
    {
        var activities = new List<Activity>();
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiClients.TelemetrySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return activities;
    }

    private static bool Carries(Activity activity, string marker) =>
        activity.TagObjects.Any(t => (t.Value?.ToString() ?? "").Contains(marker, StringComparison.Ordinal))
        || activity.Events.SelectMany(e => e.Tags).Any(t => (t.Value?.ToString() ?? "").Contains(marker, StringComparison.Ordinal));

    [Fact]
    public async Task Without_LogContent_no_prompt_or_answer_reaches_any_log_sink_even_at_Trace()
    {
        var (provider, logs) = Build(logContent: false);
        await using var _ = provider;
        var marker = "m" + Guid.NewGuid().ToString("N");
        var activities = Listen(out var listener);
        using (listener)
        {
            await CallEverythingAsync(provider, marker);
        }

        logs.Entries.ShouldNotContain(e => e.Text.Contains(marker, StringComparison.Ordinal), "no prompt or answer text in any log");
        lock (activities)
        {
            activities.ShouldNotContain(a => Carries(a, marker), "no prompt or answer text in any span");
        }

        // One structured line per model call stays: model, tokens and duration, no text.
        var calls = logs.Entries.Where(e => e.Category.EndsWith("ModelCallLogging", StringComparison.Ordinal) && e.Level == LogLevel.Information).ToList();
        calls.Count.ShouldBe(4, "answer, streamed answer, utility, embedding");
        calls.ShouldAllBe(c => c.Text.Contains("Model=", StringComparison.Ordinal) && c.Text.Contains("ElapsedMs=", StringComparison.Ordinal));
        calls.ShouldContain(c => c.Text.Contains("InputTokens=", StringComparison.Ordinal) && c.Text.Contains("OutputTokens=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_LogContent_the_prompt_reaches_the_logs_and_the_spans()
    {
        var (provider, logs) = Build(logContent: true);
        await using var _ = provider;
        var marker = "m" + Guid.NewGuid().ToString("N");
        var activities = Listen(out var listener);
        using (listener)
        {
            await CallEverythingAsync(provider, marker);
        }

        provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer).GetService<OpenTelemetryChatClient>()!.EnableSensitiveData.ShouldBeTrue();
        provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility).GetService<OpenTelemetryChatClient>()!.EnableSensitiveData.ShouldBeTrue();
        logs.Entries.ShouldContain(e => e.Text.Contains(marker, StringComparison.Ordinal), "the content log is on");
        lock (activities)
        {
            activities.ShouldContain(a => Carries(a, marker), "the spans carry the content");
        }
    }

    [Fact]
    public void Answer_pipeline_order_is_guard_limits_function_invocation_telemetry_logging_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(new ConfigurationBuilder().AddInMemoryCollection(Shipped).Build());
        services.AddSingleton<IAnswerGuardrail, PassThroughGuardrail>();
        using var provider = services.BuildServiceProvider();

        Chain(provider.GetRequiredKeyedService<IChatClient>(AiClients.Answer)).ShouldBe(
        [
            nameof(PassThroughGuardrail.Stage), nameof(ConfigureOptionsChatClient), nameof(FunctionInvokingChatClient),
            nameof(OpenTelemetryChatClient), "ModelCallLoggingChatClient", "FakeChatClient",
        ]);
        Chain(provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility)).ShouldBe(
            [nameof(ConfigureOptionsChatClient), nameof(OpenTelemetryChatClient), "ModelCallLoggingChatClient", "FakeChatClient"]);
    }

    [Fact]
    public void With_LogContent_the_content_logger_sits_between_the_call_log_and_the_provider()
    {
        var (provider, _) = Build(logContent: true);
        using (provider)
        {
            Chain(provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility)).ShouldBe(
                [nameof(ConfigureOptionsChatClient), nameof(OpenTelemetryChatClient), "ModelCallLoggingChatClient", nameof(LoggingChatClient), "FakeChatClient"]);
        }
    }

    /// <summary>The type names from the outermost client inwards, following each <see cref="DelegatingChatClient"/>'s inner client.</summary>
    private static List<string> Chain(IChatClient client)
    {
        var inner = typeof(DelegatingChatClient).GetProperty("InnerClient", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var names = new List<string>();
        for (IChatClient? current = client; current is not null; current = current is DelegatingChatClient ? (IChatClient?)inner.GetValue(current) : null)
        {
            names.Add(current.GetType().Name);
        }

        return names;
    }

    private sealed class PassThroughGuardrail : IAnswerGuardrail
    {
        public IChatClient Wrap(IChatClient inner) => new Stage(inner);

        public sealed class Stage(IChatClient inner) : DelegatingChatClient(inner);
    }
}
