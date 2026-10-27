namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: everything that will be paid for the order has been paid (decimal Rand, ZAR).</summary>
public sealed record PurchaseOrderSettled(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    decimal PaidInclVat,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
