namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: the supplier rejected the order.</summary>
public sealed record PurchaseOrderRejected(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    string OrderNumber,
    string Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
