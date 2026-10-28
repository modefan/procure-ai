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
/// <c>budget</c>, the order ID for <c>order</c>, the invoice ID for <c>invoice</c>.
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

    public static IReadOnlyList<string> All { get; } = [Request, Approval, Budget, Order, Invoice];
}

/// <summary>The live-updates hub as clients see it (through the gateway: <c>/hubs/live</c>).</summary>
public static class LiveHubContract
{
    public const string Path = "/hubs/live";

    /// <summary>The client method that receives an <see cref="EntityChanged"/>.</summary>
    public const string EntityChangedMethod = "EntityChanged";
}
