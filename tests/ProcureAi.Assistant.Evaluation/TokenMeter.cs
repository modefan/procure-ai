using System.Diagnostics.Metrics;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.Evaluation;

/// <summary>The tokens a run used, by kind: chat input and output, and embedding input.</summary>
public sealed record TokenTotals(long ChatInput, long ChatOutput, long EmbeddingInput)
{
    public override string ToString() => $"tokens: chat {ChatInput} in / {ChatOutput} out, embeddings {EmbeddingInput}";
}

/// <summary>
/// Adds up the <c>gen_ai.client.token.usage</c> histogram that the Assistant's OpenTelemetry stages record for every model call (meter
/// <see cref="AiClients.TelemetrySource"/>), so a run reports what it spent without a provider-specific counter.
/// </summary>
public sealed class TokenMeter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _chatInput;
    private long _chatOutput;
    private long _embeddingInput;

    public TokenMeter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AiClients.TelemetrySource && instrument.Name == "gen_ai.client.token.usage")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((_, value, tags, _) => Add(value, tags));
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Add(value, tags));
        _listener.Start();
    }

    public TokenTotals Snapshot() =>
        new(Interlocked.Read(ref _chatInput), Interlocked.Read(ref _chatOutput), Interlocked.Read(ref _embeddingInput));

    private void Add(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? type = null;
        string? operation = null;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "gen_ai.token.type":
                    type = tag.Value as string;
                    break;
                case "gen_ai.operation.name":
                    operation = tag.Value as string;
                    break;
            }
        }

        if (operation == "embeddings")
        {
            Interlocked.Add(ref _embeddingInput, value);
        }
        else if (type == "output")
        {
            Interlocked.Add(ref _chatOutput, value);
        }
        else
        {
            Interlocked.Add(ref _chatInput, value);
        }
    }

    public void Dispose() => _listener.Dispose();
}
