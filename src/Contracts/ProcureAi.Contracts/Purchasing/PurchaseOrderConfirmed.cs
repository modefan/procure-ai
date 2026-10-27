namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: the supplier confirmed the order (by webhook).</summary>
public sealed record PurchaseOrderConfirmed(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    string OrderNumber,
    string SupplierReference,
    DateOnly? ExpectedDelivery,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
