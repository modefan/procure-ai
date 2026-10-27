namespace ProcureAi.Contracts.Purchasing;

/// <summary>Integration event: goods were received against an order.</summary>
public sealed record GoodsReceived(
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    Guid ReceiptId,
    IReadOnlyList<ReceivedLine> Lines,
    string ReceivedBy,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
