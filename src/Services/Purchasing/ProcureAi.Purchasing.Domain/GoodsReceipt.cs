namespace ProcureAi.Purchasing.Domain;

/// <summary>A delivery (possibly partial) booked against an order.</summary>
public sealed class GoodsReceipt
{
    private GoodsReceipt()
    {
    }

    internal GoodsReceipt(Guid id, string receivedBy, DateTimeOffset receivedAt, string? deliveryNote, IReadOnlyList<ReceiptLine> lines)
    {
        Id = id;
        ReceivedBy = receivedBy;
        ReceivedAt = receivedAt;
        DeliveryNote = deliveryNote;
        Lines = [.. lines];
    }

    public Guid Id { get; private set; }

    public string ReceivedBy { get; private set; } = "";

    public DateTimeOffset ReceivedAt { get; private set; }

    public string? DeliveryNote { get; private set; }

    public IReadOnlyList<ReceiptLine> Lines { get; private set; } = [];
}

/// <summary>How many of an order line a receipt brought in.</summary>
public sealed record ReceiptLine(int LineNo, int Quantity);
