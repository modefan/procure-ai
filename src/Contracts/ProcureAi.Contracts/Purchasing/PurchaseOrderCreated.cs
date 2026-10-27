namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: a purchase order was created (amounts decimal Rand, ZAR).</summary>
public sealed record PurchaseOrderCreated(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    string OrderNumber,
    string RequestNumber,
    string SupplierId,
    string CostCenterCode,
    string RequesterId,
    IReadOnlyList<OrderLineItem> Lines,
    decimal TotalExclVat,
    decimal Vat,
    decimal TotalInclVat,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
