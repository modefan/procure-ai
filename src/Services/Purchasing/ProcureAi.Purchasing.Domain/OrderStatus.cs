namespace ProcureAi.Purchasing.Domain;

public enum OrderStatus
{
    /// <summary>Created, not yet accepted by the supplier's API (being sent or retried).</summary>
    PendingSupplier,

    /// <summary>The supplier's API accepted it; waiting for the confirmation webhook.</summary>
    SentToSupplier,

    Confirmed,
    Rejected,
    PartiallyReceived,
    Received,
    ClosedShort,
    Cancelled,
}
