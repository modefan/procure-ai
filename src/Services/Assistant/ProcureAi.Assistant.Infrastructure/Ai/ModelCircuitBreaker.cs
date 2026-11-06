using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Web.Status;
using CircuitState = ProcureAi.BuildingBlocks.Application.Status.CircuitState;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The circuit breakers in front of the model calls (<c>AI:Resilience</c>, <see cref="AiResilienceOptions"/>): one Polly v8 pipeline per
/// model pipeline, registered with Microsoft.Extensions.Resilience (so its state changes are traced and metered like every other
/// resilience pipeline). Only a passing provider failure counts (<see cref="AiFailures"/>: timeout, no answer, 408, 429, 5xx); the
/// caller's own cancellation and a refusal (400, 401) do not. An open breaker throws <see cref="BrokenCircuitException"/>, which is an
/// <see cref="ExecutionRejectedException"/> and so already a transient failure everywhere: the SSE <c>error</c> event of an answer, the
/// 503 with <c>Retry-After</c> of a scan, and the durable retry rules of ingestion and the purchase index.
/// <para>
/// The calls run through the pipeline as <b>void</b> callbacks that hand their result back through a <see cref="Box{T}"/>: Polly's telemetry
/// logs a callback's result (at Debug), and a <see cref="ChatResponse"/> prints its text, which may only be logged with
/// <c>AI:Telemetry:LogContent</c> (AiContentLoggingTests).
/// </para>
/// </summary>
internal static class ModelCircuitBreaker
{
    public const string Answer = "assistant-ai-answer";
    public const string Utility = "assistant-ai-utility";
    public const string Guard = "assistant-ai-guard";
    public const string Embeddings = "assistant-ai-embeddings";

    private static readonly AiFailures Failures = new();

    /// <summary>The name of each pipeline's breaker on the system status screen.</summary>
    public static IReadOnlyDictionary<string, string> StatusNames { get; } = new Dictionary<string, string>
    {
        [Answer] = "AI model (answer)",
        [Utility] = "AI model (utility)",
        [Guard] = "AI model (guard)",
        [Embeddings] = "AI model (embeddings)",
    };

    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        foreach (var name in new[] { Answer, Utility, Guard, Embeddings })
        {
            var statusName = StatusNames[name];
            services.AddKnownCircuitBreaker(statusName);
            services.AddResiliencePipeline(name, (builder, context) =>
            {
                var settings = context.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value.Resilience;
                var states = context.ServiceProvider.GetRequiredService<ICircuitBreakerStates>();
                var time = context.ServiceProvider.GetRequiredService<TimeProvider>();
                var circuit = new CircuitBreakerStateProvider();
                builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    Name = name,
                    FailureRatio = settings.FailureRatio,
                    MinimumThroughput = settings.MinimumThroughput,
                    SamplingDuration = settings.SamplingDuration,
                    BreakDuration = settings.BreakDuration,
                    StateProvider = circuit,
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is { } failure
                        && (Counts(failure, args.Context.CancellationToken)
                            || IsCancelledProbe(circuit.CircuitState, args.Context.CancellationToken))),
                    // The status screen's view of the breaker (ICircuitBreakerStates), with the time the next trial call is let through.
                    OnOpened = args =>
                    {
                        states.Report(statusName, CircuitState.Open, time.GetUtcNow() + args.BreakDuration);
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = _ =>
                    {
                        states.Report(statusName, CircuitState.HalfOpen, null);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = _ =>
                    {
                        states.Report(statusName, CircuitState.Closed, null);
                        return ValueTask.CompletedTask;
                    },
                });
            });
        }
    }

    public static ResiliencePipeline Pipeline(IServiceProvider services, string name) =>
        services.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline(name);

    /// <summary>
    /// Whether a failed model call counts towards opening the breaker: a passing provider failure the caller did not cause. Once the
    /// caller has cancelled, nothing counts, whatever the provider threw: an SDK may surface the aborted request as a transport
    /// failure (an <see cref="HttpRequestException"/> or <see cref="IOException"/>) rather than as a cancellation.
    /// </summary>
    public static bool Counts(Exception failure, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && failure is not BrokenCircuitException
        && Failures.IsTransient(failure);

    /// <summary>
    /// A half-open trial call that failed because the caller cancelled it. It says nothing about the provider, but Polly closes the
    /// breaker on every outcome it does not handle, so it is handled as a failure: the breaker opens again for one more
    /// <see cref="AiResilienceOptions.BreakDuration"/> instead of closing on a provider that may still be down (plan 05 ruling 60).
    /// While closed, a cancelled call still never counts (<see cref="Counts"/>).
    /// </summary>
    public static bool IsCancelledProbe(Polly.CircuitBreaker.CircuitState state, CancellationToken cancellationToken) =>
        state == Polly.CircuitBreaker.CircuitState.HalfOpen && cancellationToken.IsCancellationRequested;

    /// <summary>Runs <paramref name="call"/> through <paramref name="pipeline"/> without handing its result to the pipeline (so never to its logs).</summary>
    public static async Task<T> RunAsync<T>(ResiliencePipeline pipeline, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var box = new Box<T>();
        await pipeline.ExecuteAsync(
            static async (state, token) => { state.Box.Value = await state.Call(token); }, (Box: box, Call: call), cancellationToken);
        return box.Value!;
    }

    /// <summary>Records a failure that happened outside a breaker call (mid-stream), so it still counts. An open breaker is ignored here.</summary>
    public static async Task ReportAsync(ResiliencePipeline pipeline, Exception failure, CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.ExecuteAsync(static (failed, _) => ValueTask.FromException(failed), failure, cancellationToken);
        }
        catch
        {
            // The same failure (now counted) or a refusal of an already open breaker: the caller throws the original either way.
        }
    }
}

/// <summary>
/// A chat pipeline stage that runs every call through its circuit breaker (<see cref="ModelCircuitBreaker"/>). A stream counts once:
/// it succeeds or fails at its first update (a provider that is down fails there); a failure later in the stream is reported to the
/// breaker as well.
/// </summary>
internal sealed class CircuitBreakerChatClient(IChatClient inner, ResiliencePipeline pipeline) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        ModelCircuitBreaker.RunAsync(pipeline, token => base.GetResponseAsync(messages, options, token), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);

        // The first update through the breaker: an open breaker refuses here, before the provider is asked.
        var more = await ModelCircuitBreaker.RunAsync(pipeline, async _ => await updates.MoveNextAsync(), cancellationToken);
        while (more)
        {
            yield return updates.Current;
            try
            {
                more = await updates.MoveNextAsync();
            }
            catch (Exception failure) when (ModelCircuitBreaker.Counts(failure, cancellationToken))
            {
                await ModelCircuitBreaker.ReportAsync(pipeline, failure, CancellationToken.None);
                throw;
            }
        }
    }
}

/// <summary>The embedding generator's stage that runs every call through its circuit breaker (<see cref="ModelCircuitBreaker"/>).</summary>
internal sealed class CircuitBreakerEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ResiliencePipeline pipeline)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        ModelCircuitBreaker.RunAsync(pipeline, token => base.GenerateAsync(values, options, token), cancellationToken);
}

/// <summary>Carries a call's result past the resilience pipeline (see <see cref="ModelCircuitBreaker"/>).</summary>
internal sealed class Box<T>
{
    public T? Value { get; set; }
}
