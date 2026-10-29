using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Telemetry;

namespace ProcureAi.Assistant.Tests.Guardrails;

/// <summary>An inner model that streams the given pieces, counting calls, the pieces read and whether its stream was disposed.</summary>
internal sealed class ScriptedChatClient(params string[] pieces) : IChatClient
{
    public int Calls { get; private set; }

    /// <summary>The options of the last call, as the inner client got them.</summary>
    public ChatOptions? LastOptions { get; private set; }

    public int PiecesRead { get; private set; }

    public bool StreamDisposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(pieces))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        LastOptions = options;
        try
        {
            foreach (var piece in pieces)
            {
                await Task.Yield();
                PiecesRead++;
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            }
        }
        finally
        {
            StreamDisposed = true;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>Counts the <c>assistant.guard_blocks</c> measurements by stage and reason.</summary>
internal sealed class GuardBlockCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Stage, string Reason)> _seen = new();

    public GuardBlockCounter(IMeterFactory meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == AssistantMetrics.GuardBlocks && instrument.Meter.Name == AssistantMetrics.MeterName
                && ReferenceEquals(instrument.Meter.Scope, meters))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");
            for (var i = 0; i < value; i++)
            {
                _seen.Enqueue((map["stage"], map["reason"]));
            }
        });
        _listener.Start();
    }

    public int Count(string stage, string reason) => _seen.Count(s => s.Stage == stage && s.Reason == reason);

    public int Total => _seen.Count;

    public void Dispose() => _listener.Dispose();
}

/// <summary>A logger that keeps every formatted message with its structured values.</summary>
internal sealed class CapturingLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
        Entries.Enqueue((logLevel, formatter(state, exception) + " || " + values));
    }
}

internal static class GuardTestServices
{
    /// <summary>A metrics instance on a meter factory of its own (so parallel tests never see each other's measurements).</summary>
    public static (ServiceProvider Provider, AssistantMetrics Metrics, IMeterFactory Meters) Metrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = provider.GetRequiredService<IMeterFactory>();
        return (provider, new AssistantMetrics(meters), meters);
    }
}
