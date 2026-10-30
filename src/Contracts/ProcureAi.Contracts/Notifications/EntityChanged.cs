namespace ProcureAi.Contracts.Notifications;

/// <summary>
/// Pushed by the Notifications service over SignalR (hub <see cref="LiveHubContract.Path"/>, client method <see cref="LiveHubContract.EntityChangedMethod"/>)
/// to every client of the tenant when something changed. It carries IDs only: the client refetches through the authorized APIs, so a
/// push never shows data the reader may not see. The JSON is camelCase (<c>entity</c>, <c>id</c>, <c>requestId</c>, <c>eventType</c>,
/// <c>occurredAt</c>). Not an integration event: it never travels over RabbitMQ.
/// </summary>
/// <param name="Entity">What changed, one of <see cref="LiveEntities"/>.</param>
/// <param name="Id">
/// Its ID: the request ID for <c>request</c> and <c>approval</c> (the approval chain of that request), the cost center code for
/// <c>budget</c>, the order ID for <c>order</c>, the invoice ID for <c>invoice</c>, the document ID for <c>policy-document</c>, the tenant ID for <c>access-matrix</c>.
/// </param>
/// <param name="RequestId">The request the change belongs to, when there is one.</param>
/// <param name="EventType">The integration event behind it, e.g. <c>RequestSubmitted</c>.</param>
/// <param name="OccurredAt">When the event happened.</param>
public sealed record EntityChanged(string Entity, string Id, Guid? RequestId, string EventType, DateTimeOffset OccurredAt);

/// <summary>The values of <see cref="EntityChanged.Entity"/>.</summary>
public static class LiveEntities
{
    public const string Request = "request";
    public const string Approval = "approval";
    public const string Budget = "budget";
    public const string Order = "order";
    public const string Invoice = "invoice";

    /// <summary>A policy document of the Assistant (its ingestion progress and status).</summary>
    public const string PolicyDocument = "policy-document";

    /// <summary>The role-permission matrix of the tenant (ID: the tenant ID). Admin screens refetch it.</summary>
    public const string AccessMatrix = "access-matrix";

    public static IReadOnlyList<string> All { get; } = [Request, Approval, Budget, Order, Invoice, PolicyDocument, AccessMatrix];
}

/// <summary>The live-updates hub as clients see it (through the gateway: <c>/hubs/live</c>).</summary>
public static class LiveHubContract
{
    public const string Path = "/hubs/live";

    /// <summary>The client method that receives an <see cref="EntityChanged"/>.</summary>
    public const string EntityChangedMethod = "EntityChanged";

    /// <summary>
    /// Client method <c>(entity, id, viewers)</c>: the distinct users (<c>{ userId, name }</c>) now viewing that entity, sent to everyone
    /// watching it whenever the list may have changed.
    /// </summary>
    public const string ViewersChangedMethod = "ViewersChanged";

    /// <summary>Client method <c>(users)</c>: the distinct users (<c>{ userId, name }</c>) of the tenant now online.</summary>
    public const string OnlineChangedMethod = "OnlineChanged";

    /// <summary>Client method <c>(item)</c>: a new bell item of the connection's user (<c>{ id, text, requestId, link, createdAt, read }</c>).</summary>
    public const string NotificationAddedMethod = "NotificationAdded";

    /// <summary>Server method <c>Heartbeat()</c>: keeps the connection online (and its watched entities) for another TTL.</summary>
    public const string HeartbeatMethod = "Heartbeat";

    /// <summary>Server method <c>Watch(entity, id)</c>: the caller views that entity; returns the current viewers.</summary>
    public const string WatchMethod = "Watch";

    /// <summary>Server method <c>Unwatch(entity, id)</c>: the caller no longer views that entity.</summary>
    public const string UnwatchMethod = "Unwatch";
}
