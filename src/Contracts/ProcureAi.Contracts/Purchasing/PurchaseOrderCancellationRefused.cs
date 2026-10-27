namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: an order could not be cancelled (for example it has shipped).</summary>
public sealed record PurchaseOrderCancellationRefused(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    string Reason,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
