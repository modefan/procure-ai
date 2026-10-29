using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// One structured log line per model call: model, input and output tokens, duration and outcome, never any prompt or answer text
/// (plan 05 Global Constraint). It is the logging stage of every pipeline; Microsoft.Extensions.AI's own <see cref="LoggingChatClient"/>,
/// which writes the messages at Trace level, is added below it only when <c>AI:Telemetry:LogContent</c> is true, so the option decides,
/// not the log level. Log category <c>ProcureAi.Assistant.ModelCallLogging</c>.
/// </summary>
public static partial class ModelCallLogging
{
    public const string Category = "ProcureAi.Assistant.ModelCallLogging";

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Model call {Operation} {Model} {Outcome} in {ElapsedMs} ms, {InputTokens} input / {OutputTokens} output tokens")]
    internal static partial void Call(
        ILogger logger, string operation, string model, string outcome, long elapsedMs, long? inputTokens, long? outputTokens);

    internal static string Outcome(Exception exception) => exception is OperationCanceledException ? "cancelled" : "failed";
}

/// <summary>The chat logging stage: <see cref="ModelCallLogging"/>.</summary>
internal sealed class ModelCallLoggingChatClient(IChatClient inner, ILogger logger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            Log("chat", response.ModelId ?? options?.ModelId, "ok", started, response.Usage);
            return response;
        }
        catch (Exception exception)
        {
            Log("chat", options?.ModelId, ModelCallLogging.Outcome(exception), started, null);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        string? model = null;
        UsageDetails? usage = null;
        var outcome = "failed";
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                model ??= update.ModelId;
                foreach (var content in update.Contents.OfType<UsageContent>())
                {
                    usage = content.Details;
                }

                yield return update;
            }

            outcome = "ok";
        }
        finally
        {
            if (outcome != "ok" && cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
            }

            Log("chat-stream", model ?? options?.ModelId, outcome, started, usage);
        }
    }

    private void Log(string operation, string? model, string outcome, long started, UsageDetails? usage) =>
        ModelCallLogging.Call(
            logger, operation, model ?? "unknown", outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            usage?.InputTokenCount, usage?.OutputTokenCount);
}

/// <summary>The embedding logging stage: <see cref="ModelCallLogging"/>.</summary>
internal sealed class ModelCallLoggingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ILogger logger)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var embeddings = await base.GenerateAsync(values, options, cancellationToken);
            ModelCallLogging.Call(
                logger, "embeddings", embeddings.FirstOrDefault()?.ModelId ?? options?.ModelId ?? "unknown", "ok",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, embeddings.Usage?.InputTokenCount, null);
            return embeddings;
        }
        catch (Exception exception)
        {
            ModelCallLogging.Call(
                logger, "embeddings", options?.ModelId ?? "unknown", ModelCallLogging.Outcome(exception),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, null);
            throw;
        }
    }
}
