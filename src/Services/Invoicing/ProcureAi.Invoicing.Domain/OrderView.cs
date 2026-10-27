using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>One line of an order as Invoicing knows it. Received, closed-short and paid quantities change only through the order view.</summary>
public sealed class OrderViewLine
{
    private OrderViewLine()
    {
    }

    public OrderViewLine(int lineNo, string sku, string description, int ordered, Money unitPriceExclVat)
    {
        if (lineNo < 1)
        {
            throw new DomainException("A line number must be 1 or more.");
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("A line needs a SKU.");
        }

        if (ordered < 1)
        {
            throw new DomainException($"Line {lineNo}: the quantity must be positive.");
        }

        LineNo = lineNo;
        Sku = sku.Trim();
        Description = description?.Trim() ?? "";
        Ordered = ordered;
        UnitPriceExclVat = unitPriceExclVat;
    }

    public int LineNo { get; private set; }

    public string Sku { get; private set; } = "";

    public string Description { get; private set; } = "";

    public int Ordered { get; private set; }

    public Money UnitPriceExclVat { get; private set; }

    public int Received { get; private set; }

    public int ClosedShort { get; private set; }

    public int Paid { get; private set; }

    internal void AddReceived(int quantity) => Received += quantity;

    internal void AddClosedShort(int quantity) => ClosedShort += quantity;

    internal void AddPaid(int quantity) => Paid += quantity;
}

/// <summary>
/// Invoicing's copy of a purchase order, kept up to date from Purchasing's events. Every payment and every match writes this
/// aggregate (<see cref="Revision"/> changes), so its version column serialises everything that happens to one order.
/// </summary>
public sealed class OrderView : Entity<Guid>
{
    private readonly List<OrderViewLine> _lines = [];

    private OrderView()
    {
    }

    public string TenantId { get; private set; } = "";

    public Guid RequestId { get; private set; }

    /// <summary>The order number as text, such as <c>PO-1001</c>.</summary>
    public string OrderNumber { get; private set; } = "";

    public string SupplierId { get; private set; } = "";

    public string CostCenterCode { get; private set; } = "";

    /// <summary>The user who raised the request: they may not release its payments (separation of duties).</summary>
    public string RequesterId { get; private set; } = "";

    public OrderViewStatus Status { get; private set; }

    public IReadOnlyList<OrderViewLine> Lines => _lines.AsReadOnly();

    /// <summary>What was paid in total, including VAT (each payment's own VAT, rounded per payment).</summary>
    public Money PaidInclVat { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Counts matches and payments, so each of them changes this row and moves the version.</summary>
    public int Revision { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    /// <summary>Not cancelled and every line is paid or closed short.</summary>
    public bool IsSettled => Status != OrderViewStatus.Cancelled && _lines.All(l => l.Paid + l.ClosedShort >= l.Ordered);

    public static OrderView Create(
        Guid orderId, string tenantId, Guid requestId, string orderNumber, string supplierId, string costCenterCode,
        string requesterId, IReadOnlyList<OrderViewLine> lines)
    {
        Require(tenantId, "Tenant");
        Require(orderNumber, "Order number");
        Require(supplierId, "Supplier");
        Require(requesterId, "Requester");
        if (lines is null || lines.Count == 0)
        {
            throw new DomainException("An order needs at least one line.");
        }

        if (lines.Select(l => l.LineNo).Distinct().Count() != lines.Count)
        {
            throw new DomainException("Line numbers must be unique.");
        }

        var view = new OrderView
        {
            Id = orderId,
            TenantId = tenantId,
            RequestId = requestId,
            OrderNumber = orderNumber,
            SupplierId = supplierId,
            CostCenterCode = costCenterCode?.Trim() ?? "",
            RequesterId = requesterId,
            Status = OrderViewStatus.Open,
        };
        view._lines.AddRange(lines);
        return view;
    }

    public void ApplyReceipt(IReadOnlyList<(int LineNo, int Quantity)> received)
    {
        foreach (var (lineNo, quantity) in received)
        {
            var line = Find(lineNo);
            if (quantity < 1 || line.Received + line.ClosedShort + quantity > line.Ordered)
            {
                throw new DomainException($"Line {lineNo}: {quantity} received does not fit the order.");
            }
        }

        foreach (var (lineNo, quantity) in received)
        {
            Find(lineNo).AddReceived(quantity);
        }

        Revision++;
    }

    public void ApplyClosedShort(IReadOnlyList<(int LineNo, int Quantity)> closed)
    {
        foreach (var (lineNo, quantity) in closed)
        {
            var line = Find(lineNo);
            if (quantity < 1 || line.Received + line.ClosedShort + quantity > line.Ordered)
            {
                throw new DomainException($"Line {lineNo}: {quantity} closed short does not fit the order.");
            }
        }

        foreach (var (lineNo, quantity) in closed)
        {
            Find(lineNo).AddClosedShort(quantity);
        }

        Revision++;
    }

    public void Cancel()
    {
        Status = OrderViewStatus.Cancelled;
        Revision++;
    }

    /// <summary>Marks the order settled. True only the first time it is settled, so the event goes out once.</summary>
    public bool MarkSettled(DateTimeOffset now)
    {
        if (!IsSettled || SettledAt is not null)
        {
            return false;
        }

        SettledAt = now;
        Revision++;
        return true;
    }

    /// <summary>A match was worked out against this order: moves the version so matches and payments of one order never overlap.</summary>
    public void RecordMatch() => Revision++;

    internal void RecordPayment(IReadOnlyList<(int LineNo, int Quantity)> lines, Money totalInclVat)
    {
        foreach (var (lineNo, quantity) in lines)
        {
            var line = Find(lineNo);
            if (quantity < 1 || line.Paid + quantity > line.Received)
            {
                throw new DomainException($"Line {lineNo}: {quantity} cannot be paid, only {line.Received - line.Paid} received and unpaid.");
            }
        }

        foreach (var (lineNo, quantity) in lines)
        {
            Find(lineNo).AddPaid(quantity);
        }

        PaidInclVat += totalInclVat;
        Revision++;
    }

    private OrderViewLine Find(int lineNo) =>
        _lines.FirstOrDefault(l => l.LineNo == lineNo) ?? throw new DomainException($"The order has no line {lineNo}.");

    private static void Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{what} is required.");
        }
    }
}
