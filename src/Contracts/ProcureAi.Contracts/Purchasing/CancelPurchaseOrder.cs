namespace ProcureAi.Contracts.Purchasing;

/// <summary>Command to Purchasing: cancel the order of a request (before it is shipped), or leave a tombstone if none exists yet.</summary>
public sealed record CancelPurchaseOrder(
    Guid RequestId,
    string TenantId,
    string Reason);
