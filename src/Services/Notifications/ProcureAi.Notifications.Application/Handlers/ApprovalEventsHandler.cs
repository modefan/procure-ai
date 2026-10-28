using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live updates for the events of the <c>approvals</c> exchange: each event becomes the pushes <see cref="LiveEventMap"/> names, sent to its tenant.
/// One method per event type: Wolverine 6.53 hides an interface handler behind any type-specific one, so nothing is left to it.
/// The events with a person to tell (plan 04 ruling 8: approvers due, the requester) also become a bell item and an email
/// (<see cref="IPersonalNotifier"/>), after the live push. The message ID makes a redelivery add no second item and send no second email.
/// </summary>
public static class ApprovalEventsHandler
{
    public static async Task Handle(
        ApprovalRequested message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static Task Handle(ApprovalStepApproved message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static async Task Handle(
        ApprovalReminderDue message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static async Task Handle(
        ApprovalEscalated message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static Task Handle(ApprovalAlertRaised message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static async Task Handle(
        RequestApproved message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static async Task Handle(
        RequestRejected message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static Task Handle(RequestOrdered message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static async Task Handle(
        RequestCompleted message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }

    public static Task Handle(RequestCancellationConfirmed message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static async Task Handle(
        RequestCancellationRefused message, ILivePushes pushes, IPersonalNotifier personal, IMessageMetadata metadata, CancellationToken cancellationToken)
    {
        await pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
        await personal.NotifyAsync(message, metadata.MessageId, cancellationToken);
    }
}
