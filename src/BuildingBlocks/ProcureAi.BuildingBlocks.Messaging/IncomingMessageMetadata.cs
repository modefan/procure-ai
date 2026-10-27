using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// <see cref="IMessageMetadata"/> over the envelope being handled (<see cref="IncomingMessage"/>, set by
/// <see cref="RestoreCallerMiddleware"/>). Registered scoped as a class because Wolverine's generated handler code constructs it.
/// </summary>
public sealed class IncomingMessageMetadata(IncomingMessage incoming) : IMessageMetadata
{
    public Guid? MessageId => incoming.Envelope?.Id;

    public string? CorrelationId =>
        incoming.Envelope is { } envelope
        && envelope.Headers.TryGetValue(CallerHeaders.CorrelationId, out var value)
        && !string.IsNullOrEmpty(value)
            ? value
            : null;
}
