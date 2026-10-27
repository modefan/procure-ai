namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: an order was cancelled. <c>OrderId</c> is null when the cancel came before the order existed (a tombstone is left).</summary>
public sealed record PurchaseOrderCancelled(
    Guid? OrderId,
    Guid RequestId,
    string TenantId,
    string Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
