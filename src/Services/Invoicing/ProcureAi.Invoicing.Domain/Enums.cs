namespace ProcureAi.Invoicing.Domain;

/// <summary>Whether Invoicing still expects goods and payments against an order.</summary>
public enum OrderViewStatus
{
    Open,
    Cancelled,
}

/// <summary>How an invoice reached us.</summary>
public enum InvoiceSource
{
    Webhook,
    Manual,
    Scan,
}

public enum InvoiceStatus
{
    /// <summary>The order is not known here yet; the invoice is matched when it arrives.</summary>
    AwaitingOrder,
    Matched,
    Mismatched,
    PartiallyPaid,
    Paid,
}

public enum ExportStatus
{
    Pending,
    Exported,
}
