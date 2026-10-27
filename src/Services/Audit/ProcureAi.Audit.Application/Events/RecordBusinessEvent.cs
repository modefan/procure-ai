using System.Text.Json;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;

namespace ProcureAi.Audit.Application.Events;

/// <summary>
/// Records every integration event that reaches the <c>audit-events</c> queue (bound to every exchange in
/// <see cref="Exchanges"/>) as a <see cref="BusinessEvent"/>. The user is the initiating user of the message headers
/// (<see cref="ICallerContext.UserId"/>), never the service that sent it; the document ID is the message ID, so a
/// redelivery stores nothing new.
/// <para>
/// One handler takes the <see cref="IIntegrationEvent"/> interface, so a new contract is recorded without a change here.
/// Wolverine (6.53) applies a handler of an interface only to message types that have no handler of their own in the service:
/// <c>AccessChanged</c> has one (the shared <c>AccessChangedHandler</c> evicts cached Access answers), so it is recorded by an
/// explicit method, which Wolverine combines with the shared one. The integration test that publishes every contract catches
/// any other type that ever gets a handler here.
/// </para>
/// </summary>
public static class RecordBusinessEventHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task Handle(
        IIntegrationEvent @event, ICallerContext caller, IMessageMetadata message, IAuditStore store, CancellationToken cancellationToken) =>
        store.AppendAsync(From(@event, caller, message), cancellationToken);

    /// <summary><c>AccessChanged</c> has a handler of its own (cache eviction), so the interface method does not see it.</summary>
    public static Task Handle(
        AccessChanged @event, ICallerContext caller, IMessageMetadata message, IAuditStore store, CancellationToken cancellationToken) =>
        store.AppendAsync(From(@event, caller, message), cancellationToken);

    public static BusinessEvent From(IIntegrationEvent @event, ICallerContext caller, IMessageMetadata message)
    {
        var id = message.MessageId
            ?? throw new InvalidOperationException($"{@event.GetType().Name} has no message ID: only broker messages are recorded.");
        return new BusinessEvent(
            id,
            @event.TenantId,
            @event.GetType().Name,
            IntegrationEventTypes.RequestIdOf(@event),
            caller.UserId,
            message.CorrelationId,
            caller.CallingService ?? BusinessEvent.UnknownService,
            @event.OccurredAt,
            JsonSerializer.Serialize(@event, @event.GetType(), Json));
    }
}
