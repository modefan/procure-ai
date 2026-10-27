namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: an order was closed with goods outstanding. <c>ClosedLines</c> holds the outstanding quantity per line.</summary>
public sealed record PurchaseOrderClosedShort(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    IReadOnlyList<ReceivedLine> ClosedLines,
    string Reason,
    string ClosedBy,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
