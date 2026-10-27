namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// The broker message a handler is consuming, for handlers that record messages (the Audit service). Both values are null
/// outside a message handler.
/// </summary>
public interface IMessageMetadata
{
    /// <summary>
    /// The message's ID as the sender assigned it (Wolverine's envelope ID, carried as the AMQP <c>message-id</c>). Every
    /// delivery of the same message has the same ID, so it identifies the message across redeliveries.
    /// </summary>
    Guid? MessageId { get; }

    /// <summary>The correlation ID of the request that caused the message, when the sender had one.</summary>
    string? CorrelationId { get; }
}
