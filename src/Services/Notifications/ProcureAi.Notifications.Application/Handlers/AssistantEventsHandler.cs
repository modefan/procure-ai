using ProcureAi.Contracts.Assistant;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Handlers;

/// <summary>
/// Live updates for the events of the <c>assistant</c> exchange: a policy document's ingestion progress and status become an
/// <c>EntityChanged("policy-document", documentId)</c> for its tenant (<see cref="LiveEventMap"/>). One method per event type.
/// </summary>
public static class AssistantEventsHandler
{
    public static Task Handle(PolicyDocumentProgressed message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);

    public static Task Handle(PolicyDocumentStatusChanged message, ILivePushes pushes, CancellationToken cancellationToken) =>
        pushes.PushToTenantAsync(message.TenantId, LiveEventMap.For(message), cancellationToken);
}
