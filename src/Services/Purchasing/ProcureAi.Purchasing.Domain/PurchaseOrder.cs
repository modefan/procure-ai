using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Purchasing.Domain;

/// <summary>
/// An order to a supplier for an approved request: sent through the supplier API, confirmed by webhook, received in one or
/// more deliveries, closed short or cancelled. Amounts are Rand; VAT is worked out on the total.
/// </summary>
public sealed class PurchaseOrder : Entity<Guid>
{
    private readonly List<OrderLine> _lines = [];
    private readonly List<GoodsReceipt> _receipts = [];

    private PurchaseOrder()
    {
    }

    public string TenantId { get; private set; } = "";

    public Guid RequestId { get; private set; }

    /// <summary>Null only on a tombstone (a cancel that arrived before the order was created).</summary>
    public OrderNumber? Number { get; private set; }

    public string RequestNumber { get; private set; } = "";

    public string RequesterId { get; private set; } = "";

    public string CostCenterCode { get; private set; } = "";

    public string SupplierId { get; private set; } = "";

    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    public IReadOnlyList<GoodsReceipt> Receipts => _receipts.AsReadOnly();

    public OrderStatus Status { get; private set; }

    public string? SupplierReference { get; private set; }

    public DateOnly? ExpectedDelivery { get; private set; }

    /// <summary>How many times the supplier API could not be reached when sending.</summary>
    public int SupplierAttempts { get; private set; }

    public string? LastSupplierError { get; private set; }

    public DateTimeOffset? NextSupplierAttemptAt { get; private set; }

    /// <summary>Why the order was cancelled, or what the supplier gave as the reason for declining it.</summary>
    public string? CancelReason { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>The order was cancelled here but the supplier still has to be told.</summary>
    public bool SupplierCancelPending { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public Money TotalExclVat => _lines.Aggregate(Money.Zero, (sum, line) => sum + line.LineTotalExclVat);

    public Money Vat => TotalExclVat.Vat();

    public Money TotalInclVat => TotalExclVat + Vat;

    public static PurchaseOrder Create(
        string tenantId, Guid requestId, OrderNumber number, string requestNumber, string requesterId, string costCenterCode,
        string supplierId, IReadOnlyList<OrderLine> lines, DateTimeOffset now)
    {
        Require(tenantId, "Tenant");
        Require(requestNumber, "Request number");
        Require(requesterId, "Requester");
        Require(costCenterCode, "Cost center");
        Require(supplierId, "Supplier");
        if (lines is null || lines.Count == 0)
        {
            throw new DomainException("An order needs at least one line.");
        }

        if (lines.Select(l => l.LineNo).Distinct().Count() != lines.Count)
        {
            throw new DomainException("Line numbers must be unique.");
        }

        var order = new PurchaseOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RequestId = requestId,
            Number = number,
            RequestNumber = requestNumber,
            RequesterId = requesterId,
            CostCenterCode = costCenterCode.Trim(),
            SupplierId = supplierId,
            Status = OrderStatus.PendingSupplier,
            CreatedAt = now,
        };
        order._lines.AddRange(lines);
        order.Raise(new PurchaseOrderCreatedDomainEvent(order, now));
        return order;
    }

    /// <summary>
    /// The cancel of a request arrived before its order: this stores that fact (no number, no lines) so a create that follows
    /// finds the order slot taken and does nothing.
    /// </summary>
    public static PurchaseOrder CancelledBeforeCreation(string tenantId, Guid requestId, string reason, DateTimeOffset now)
    {
        Require(tenantId, "Tenant");
        Require(reason, "Reason");
        return new PurchaseOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RequestId = requestId,
            Status = OrderStatus.Cancelled,
            CancelReason = reason,
            CancelledAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>The supplier API accepted the order. Repeating it with the same reference changes nothing.</summary>
    public void MarkSent(string supplierReference, DateTimeOffset now)
    {
        Require(supplierReference, "Supplier reference");
        if (Status is OrderStatus.SentToSupplier or OrderStatus.Confirmed && SupplierReference == supplierReference)
        {
            return;
        }

        if (Status != OrderStatus.PendingSupplier)
        {
            throw new DomainException($"An order that is {Status} cannot be marked as sent.");
        }

        Status = OrderStatus.SentToSupplier;
        SupplierReference = supplierReference;
        LastSupplierError = null;
        NextSupplierAttemptAt = null;
    }

    /// <summary>The supplier API could not be reached; another attempt is planned at <paramref name="nextAttemptAt"/>.</summary>
    public void RecordSupplierFailure(string error, DateTimeOffset nextAttemptAt)
    {
        if (Status != OrderStatus.PendingSupplier)
        {
            throw new DomainException($"An order that is {Status} is not waiting to be sent.");
        }

        SupplierAttempts++;
        LastSupplierError = error;
        NextSupplierAttemptAt = nextAttemptAt;
    }

    /// <summary>
    /// The supplier confirmed (webhook). Allowed from PendingSupplier too: the webhook can arrive before our own "sent" commit.
    /// Returns false when nothing changed (already confirmed or past it, or the order was cancelled meanwhile).
    /// </summary>
    public bool Confirm(string supplierReference, DateOnly? expectedDelivery, DateTimeOffset now)
    {
        Require(supplierReference, "Supplier reference");
        switch (Status)
        {
            case OrderStatus.PendingSupplier or OrderStatus.SentToSupplier:
                Status = OrderStatus.Confirmed;
                SupplierReference = supplierReference;
                ExpectedDelivery = expectedDelivery;
                LastSupplierError = null;
                NextSupplierAttemptAt = null;
                return true;
            case OrderStatus.Rejected:
                throw new DomainException("The supplier already rejected this order.");
            default:
                return false;
        }
    }

    /// <summary>The supplier declined (webhook). Same source states as <see cref="Confirm"/>; false when nothing changed.</summary>
    public bool RejectBySupplier(string reason, DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.PendingSupplier or OrderStatus.SentToSupplier:
                Status = OrderStatus.Rejected;
                CancelReason = reason;
                LastSupplierError = null;
                NextSupplierAttemptAt = null;
                return true;
            case OrderStatus.Rejected or OrderStatus.Cancelled:
                return false;
            default:
                throw new DomainException($"The supplier cannot reject an order that is {Status}.");
        }
    }

    public GoodsReceipt Receive(IReadOnlyList<(int LineNo, int Quantity)> lines, string receivedBy, string? deliveryNote, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Confirmed or OrderStatus.PartiallyReceived))
        {
            throw new DomainException($"Goods cannot be received on an order that is {Status}.");
        }

        Require(receivedBy, "Receiver");
        if (lines is null || lines.Count == 0)
        {
            throw new DomainException("A receipt needs at least one line.");
        }

        var seen = new HashSet<int>();
        foreach (var (lineNo, quantity) in lines)
        {
            var line = _lines.FirstOrDefault(l => l.LineNo == lineNo)
                ?? throw new DomainException($"The order has no line {lineNo}.");
            if (!seen.Add(lineNo))
            {
                throw new DomainException($"Line {lineNo} appears more than once in the receipt.");
            }

            if (quantity < 1)
            {
                throw new DomainException($"Line {lineNo}: the quantity received must be positive.");
            }

            if (quantity > line.Outstanding)
            {
                throw new DomainException($"Line {lineNo}: {quantity} received but only {line.Outstanding} outstanding.");
            }
        }

        foreach (var (lineNo, quantity) in lines)
        {
            _lines.First(l => l.LineNo == lineNo).Receive(quantity);
        }

        var receipt = new GoodsReceipt(
            Guid.CreateVersion7(), receivedBy, now, string.IsNullOrWhiteSpace(deliveryNote) ? null : deliveryNote.Trim(),
            [.. lines.Select(l => new ReceiptLine(l.LineNo, l.Quantity))]);
        _receipts.Add(receipt);
        Status = _lines.All(l => l.Outstanding == 0) ? OrderStatus.Received : OrderStatus.PartiallyReceived;
        return receipt;
    }

    /// <summary>Accepts that the rest will not come. Returns what was outstanding per line (only lines with something left).</summary>
    public IReadOnlyList<(int LineNo, int Quantity)> CloseShort(string reason, string closedBy, DateTimeOffset now)
    {
        Require(reason, "Reason");
        Require(closedBy, "Closed by");
        if (Status == OrderStatus.Confirmed)
        {
            throw new DomainException("Nothing received yet: cancel the request instead.");
        }

        if (Status != OrderStatus.PartiallyReceived)
        {
            throw new DomainException($"An order that is {Status} cannot be closed short.");
        }

        var closed = new List<(int LineNo, int Quantity)>();
        foreach (var line in _lines.Where(l => l.Outstanding > 0))
        {
            closed.Add((line.LineNo, line.CloseOutstanding()));
        }

        Status = OrderStatus.ClosedShort;
        return closed;
    }

    public CancelOutcome Cancel(string reason, DateTimeOffset now)
    {
        Require(reason, "Reason");
        switch (Status)
        {
            case OrderStatus.Cancelled:
                return new CancelOutcome.AlreadyCancelled();
            case OrderStatus.Rejected:
                return new CancelOutcome.Refused("The supplier rejected the order");
            case OrderStatus.PartiallyReceived or OrderStatus.Received or OrderStatus.ClosedShort:
                return new CancelOutcome.Refused("Goods have already been received");
            default:
                Status = OrderStatus.Cancelled;
                CancelReason = reason;
                CancelledAt = now;
                NextSupplierAttemptAt = null;
                var tell = SupplierReference is not null;
                SupplierCancelPending = tell;
                return new CancelOutcome.Cancelled(tell);
        }
    }

    /// <summary>
    /// The supplier accepted the order while we were cancelling it: remember its reference and that it still has to be told.
    /// Only for an order that is cancelled.
    /// </summary>
    public void RecordAcceptedAfterCancel(string supplierReference)
    {
        Require(supplierReference, "Supplier reference");
        if (Status != OrderStatus.Cancelled)
        {
            throw new DomainException($"An order that is {Status} was not cancelled.");
        }

        SupplierReference ??= supplierReference;
        SupplierCancelPending = true;
    }

    /// <summary>
    /// The supplier confirmed an order we cancelled without ever learning its reference (the submit timed out after it reached
    /// the supplier). Records the reference and that the supplier must be told; true when the caller must send the cancel.
    /// False when nothing is needed: not cancelled, a reference is already known (the supplier was or is being told), or a
    /// tombstone.
    /// </summary>
    public bool RecordConfirmationAfterCancel(string supplierReference)
    {
        Require(supplierReference, "Supplier reference");
        if (Status != OrderStatus.Cancelled || Number is null || SupplierReference is not null)
        {
            return false;
        }

        RecordAcceptedAfterCancel(supplierReference);
        return true;
    }

    /// <summary>The supplier has been told about the cancellation.</summary>
    public void MarkSupplierCancelled() => SupplierCancelPending = false;

    private static void Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{what} is required.");
        }
    }
}
