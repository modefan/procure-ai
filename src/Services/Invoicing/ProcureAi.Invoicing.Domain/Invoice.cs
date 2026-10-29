using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>
/// A supplier invoice against an order: matched cumulatively against what was ordered and received, held by finance when in
/// doubt, and paid in one or more payments for the quantities that are payable.
/// </summary>
public sealed class Invoice : Entity<Guid>
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<Payment> _payments = [];

    private Invoice()
    {
    }

    public string TenantId { get; private set; } = "";

    public Guid OrderId { get; private set; }

    /// <summary>Empty until the order is known (<see cref="InvoiceStatus.AwaitingOrder"/>).</summary>
    public Guid RequestId { get; private set; }

    public string SupplierId { get; private set; } = "";

    public string SupplierInvoiceNumber { get; private set; } = "";

    public DateOnly InvoiceDate { get; private set; }

    /// <summary>Arrival order within the tenant (tenant sequence <c>invoice</c>); the cumulative match follows it.</summary>
    public long Sequence { get; private set; }

    public InvoiceSource Source { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    public Money StatedTotalInclVat { get; private set; }

    /// <summary>The raw webhook body or entry, as JSON.</summary>
    public string Document { get; private set; } = "{}";

    public InvoiceStatus Status { get; private set; }

    public bool OnHold { get; private set; }

    public string? HoldReason { get; private set; }

    public MatchResult? Match { get; private set; }

    public IReadOnlyList<Payment> Payments => _payments.AsReadOnly();

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Counts every state-changing operation (attach, match, hold, release, payment), so each one changes this row and moves <see cref="Version"/> even when only child rows change.</summary>
    public int Revision { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public Money TotalExclVat => _lines.Aggregate(Money.Zero, (sum, line) => sum + line.LineTotalExclVat);

    public Money Vat => TotalExclVat.Vat();

    public Money TotalInclVat => TotalExclVat + Vat;

    public static Invoice Create(
        string tenantId, Guid orderId, Guid requestId, string supplierId, string supplierInvoiceNumber, DateOnly invoiceDate,
        long sequence, InvoiceSource source, IReadOnlyList<InvoiceLine> lines, Money statedTotalInclVat, string document,
        DateTimeOffset now)
    {
        Require(tenantId, "Tenant");
        if (sequence < 1)
        {
            throw new DomainException("An invoice of a tenant needs its arrival number.");
        }

        return New(tenantId, orderId, requestId, supplierId, supplierInvoiceNumber, invoiceDate, sequence, source, lines, statedTotalInclVat, document, now);
    }

    /// <summary>
    /// An invoice the supplier sent (signed webhook) for an order Invoicing does not know yet. Its tenant is not known either: it is
    /// never taken from the payload, so the invoice has none (<see cref="UnassignedTenant"/>, so no tenant's query can see it) and no
    /// arrival number until <see cref="AssignTenant"/> gives it the tenant of the order, when the order arrives.
    /// </summary>
    public static Invoice CreateAwaitingOrder(
        Guid orderId, string supplierId, string supplierInvoiceNumber, DateOnly invoiceDate, IReadOnlyList<InvoiceLine> lines,
        Money statedTotalInclVat, string document, DateTimeOffset now) =>
        New(UnassignedTenant, orderId, Guid.Empty, supplierId, supplierInvoiceNumber, invoiceDate, 0, InvoiceSource.Webhook, lines, statedTotalInclVat, document, now);

    /// <summary>The tenant of an invoice that waits for an order Invoicing does not know yet.</summary>
    public const string UnassignedTenant = "";

    /// <summary>False while the invoice waits for its order without a tenant (<see cref="CreateAwaitingOrder"/>).</summary>
    public bool HasTenant => TenantId.Length > 0;

    /// <summary>The order of a waiting invoice arrived: the invoice takes the order's tenant and its arrival number in that tenant.</summary>
    public void AssignTenant(string tenantId, long sequence)
    {
        Require(tenantId, "Tenant");
        if (HasTenant)
        {
            throw new DomainException("The invoice already belongs to a tenant.");
        }

        if (sequence < 1)
        {
            throw new DomainException("An invoice of a tenant needs its arrival number.");
        }

        Revision++;
        TenantId = tenantId;
        Sequence = sequence;
    }

    private static Invoice New(
        string tenantId, Guid orderId, Guid requestId, string supplierId, string supplierInvoiceNumber, DateOnly invoiceDate,
        long sequence, InvoiceSource source, IReadOnlyList<InvoiceLine> lines, Money statedTotalInclVat, string document,
        DateTimeOffset now)
    {
        Require(supplierInvoiceNumber, "Invoice number");
        if (lines is null || lines.Count == 0)
        {
            throw new DomainException("An invoice needs at least one line.");
        }

        if (lines.Select(l => l.LineNo).Distinct().Count() != lines.Count)
        {
            throw new DomainException("Line numbers must be unique.");
        }

        if (statedTotalInclVat.Currency != Money.ZarCurrency || statedTotalInclVat.Amount < 0)
        {
            throw new DomainException("The invoice total must be a positive amount in ZAR.");
        }

        var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            RequestId = requestId,
            SupplierId = supplierId?.Trim() ?? "",
            SupplierInvoiceNumber = supplierInvoiceNumber.Trim(),
            InvoiceDate = invoiceDate,
            Sequence = sequence,
            Source = source,
            StatedTotalInclVat = statedTotalInclVat,
            Document = string.IsNullOrWhiteSpace(document) ? "{}" : document,
            Status = InvoiceStatus.AwaitingOrder,
            ReceivedAt = now,
        };
        invoice._lines.AddRange(lines);
        return invoice;
    }

    /// <summary>True when a repeat of the same invoice (same order, date, total and lines), as opposed to another invoice reusing the number.</summary>
    public bool HasSameContentAs(Guid orderId, DateOnly invoiceDate, IReadOnlyList<InvoiceLine> lines, Money statedTotalInclVat) =>
        OrderId == orderId
        && InvoiceDate == invoiceDate
        && StatedTotalInclVat == statedTotalInclVat
        && _lines.Count == lines.Count
        && _lines.OrderBy(l => l.LineNo).Zip(lines.OrderBy(l => l.LineNo)).All(pair =>
            pair.First.LineNo == pair.Second.LineNo
            && (pair.First.Sku.Length == 0 || pair.Second.Sku.Length == 0
                || string.Equals(pair.First.Sku, pair.Second.Sku, StringComparison.OrdinalIgnoreCase))
            && pair.First.Quantity == pair.Second.Quantity
            && pair.First.UnitPriceExclVat == pair.Second.UnitPriceExclVat);

    /// <summary>The order the invoice was waiting for is known now: take over its request, SKUs the invoice left out, and its supplier when the invoice named none.</summary>
    public void AttachOrder(OrderView order)
    {
        if (order.Id != OrderId)
        {
            throw new DomainException("That is not the order of this invoice.");
        }

        if (TenantId != order.TenantId)
        {
            throw new DomainException("The order belongs to another tenant (or the invoice has none yet).");
        }

        Revision++;
        RequestId = order.RequestId;
        foreach (var line in _lines)
        {
            if (order.Lines.FirstOrDefault(o => o.LineNo == line.LineNo) is { } orderLine)
            {
                line.TakeSkuFrom(orderLine);
            }
        }

        if (SupplierId.Length == 0)
        {
            SupplierId = order.SupplierId;
        }
    }

    /// <summary>
    /// Stores the outcome of a match. Only an invoice that is not paid at all can be matched again. Returns true when the
    /// outcome changed (first match, matched or mismatched flipped, or other reasons), which is when an event is worth publishing.
    /// </summary>
    public bool ApplyMatch(MatchResult result)
    {
        if (Status is not (InvoiceStatus.AwaitingOrder or InvoiceStatus.Matched or InvoiceStatus.Mismatched))
        {
            throw new DomainException($"An invoice that is {Status} is not matched again.");
        }

        Revision++;
        var newStatus = result.IsMatch ? InvoiceStatus.Matched : InvoiceStatus.Mismatched;
        var changed = Status != newStatus || Match is null || !Match.Reasons.SequenceEqual(result.Reasons);
        Status = newStatus;
        Match = result;
        return changed;
    }

    public void Hold(string reason)
    {
        Require(reason, "Reason");
        if (Status == InvoiceStatus.Paid)
        {
            throw new DomainException("A paid invoice cannot be put on hold.");
        }

        if (OnHold)
        {
            throw new DomainException("The invoice is already on hold.");
        }

        Revision++;
        OnHold = true;
        HoldReason = reason.Trim();
    }

    public void ReleaseHold()
    {
        if (!OnHold)
        {
            throw new DomainException("The invoice is not on hold.");
        }

        Revision++;
        OnHold = false;
        HoldReason = null;
    }

    /// <summary>
    /// Works out what a payment would pay. Per line the payable quantity is the smaller of what is unpaid on the invoice line and
    /// what is received and unpaid on the order line, and only for lines whose price passed the match. <paramref name="requested"/>
    /// null pays everything payable; chosen quantities must not exceed it. Nothing payable, or an invoice on hold, is refused.
    /// </summary>
    public PaymentPlan PlanPayment(OrderView order, IReadOnlyList<(int LineNo, int Quantity)>? requested)
    {
        if (PayRefusal(order) is { } refusal)
        {
            throw new DomainException(refusal);
        }

        var chosen = new Dictionary<int, int>();
        if (requested is not null)
        {
            foreach (var (lineNo, quantity) in requested)
            {
                if (_lines.All(l => l.LineNo != lineNo))
                {
                    throw new DomainException($"The invoice has no line {lineNo}.");
                }

                if (quantity < 1)
                {
                    throw new DomainException($"Line {lineNo}: the quantity to pay must be positive.");
                }

                if (!chosen.TryAdd(lineNo, quantity))
                {
                    throw new DomainException($"Line {lineNo} appears more than once.");
                }
            }
        }

        var planned = new List<PaymentPlanLine>();
        foreach (var line in _lines.OrderBy(l => l.LineNo))
        {
            var payable = Payable(line, order);
            int quantity;
            if (requested is null)
            {
                quantity = payable;
            }
            else if (chosen.TryGetValue(line.LineNo, out quantity) && quantity > payable)
            {
                throw new DomainException($"Line {line.LineNo}: {quantity} requested, but only {payable} can be paid.");
            }

            if (quantity > 0)
            {
                var orderLine = order.Lines.First(o => o.LineNo == line.LineNo);
                planned.Add(new PaymentPlanLine(line.LineNo, line.Sku, quantity, orderLine.UnitPriceExclVat * quantity));
            }
        }

        if (planned.Count == 0)
        {
            throw new DomainException("Nothing can be paid on this invoice yet.");
        }

        return new PaymentPlan(this, order, planned);
    }

    /// <summary>
    /// What "pay everything payable" would pay right now, or null when nothing can be paid (on hold, not matched, order closed or
    /// from another supplier, nothing received and unpaid). Same rules as <see cref="PlanPayment"/>, but a screen can ask without an
    /// exception.
    /// </summary>
    public PaymentPlan? PlanAllPayable(OrderView order)
    {
        if (PayRefusal(order) is not null)
        {
            return null;
        }

        var planned = new List<PaymentPlanLine>();
        foreach (var line in _lines.OrderBy(l => l.LineNo))
        {
            var payable = Payable(line, order);
            if (payable > 0)
            {
                var orderLine = order.Lines.First(o => o.LineNo == line.LineNo);
                planned.Add(new PaymentPlanLine(line.LineNo, line.Sku, payable, orderLine.UnitPriceExclVat * payable));
            }
        }

        return planned.Count == 0 ? null : new PaymentPlan(this, order, planned);
    }

    /// <summary>
    /// Applies a plan: the paid quantities go onto the invoice lines and onto the order view lines, in the same unit of work, so
    /// the order view's version serialises the payments of one order. The plan must have been made for this invoice.
    /// </summary>
    public Payment Pay(PaymentPlan plan, PaymentNumber number, string releasedBy, DateTimeOffset now)
    {
        Require(releasedBy, "Releaser");
        if (!ReferenceEquals(plan.Invoice, this))
        {
            throw new DomainException("That payment plan is for another invoice.");
        }

        if (OnHold)
        {
            throw new DomainException($"The invoice is on hold: {HoldReason}");
        }

        foreach (var planned in plan.Lines)
        {
            var line = _lines.First(l => l.LineNo == planned.LineNo);
            if (planned.Quantity > Payable(line, plan.Order))
            {
                throw new DomainException($"Line {planned.LineNo}: {planned.Quantity} can no longer be paid.");
            }
        }

        var quantities = plan.Lines.Select(l => (l.LineNo, l.Quantity)).ToList();
        plan.Order.RecordPayment(quantities, plan.TotalInclVat);
        foreach (var (lineNo, quantity) in quantities)
        {
            _lines.First(l => l.LineNo == lineNo).AddPaid(quantity);
        }

        Revision++;
        var payment = Payment.Create(plan, number, releasedBy.Trim(), now);
        _payments.Add(payment);
        Status = _lines.All(l => l.Paid >= l.Quantity) ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        return payment;
    }

    /// <summary>Why nothing can be paid on this invoice against this order right now (null: it can).</summary>
    private string? PayRefusal(OrderView order)
    {
        if (order.Id != OrderId)
        {
            return "That is not the order of this invoice.";
        }

        if (OnHold)
        {
            return $"The invoice is on hold: {HoldReason}";
        }

        if (Match is null || Status == InvoiceStatus.AwaitingOrder)
        {
            return "The invoice has not been matched against an order yet.";
        }

        if (order.ClosedReason is { } closed)
        {
            return closed;
        }

        return SameSupplierAs(order) ? null : SupplierReason(order);
    }

    internal bool SameSupplierAs(OrderView order) => string.Equals(SupplierId, order.SupplierId, StringComparison.Ordinal);

    internal string SupplierReason(OrderView order) => $"Invoice is from {SupplierId}, the order is with {order.SupplierId}";

    private int Payable(InvoiceLine line, OrderView order)
    {
        if (!SameSupplierAs(order))
        {
            return 0;
        }

        if (Match?.Lines.FirstOrDefault(m => m.LineNo == line.LineNo) is not { PriceOk: true })
        {
            return 0;
        }

        var orderLine = order.Lines.FirstOrDefault(o => o.LineNo == line.LineNo);
        return orderLine is null
            ? 0
            : Math.Max(0, Math.Min(line.Quantity - line.Paid, orderLine.Received - orderLine.Paid));
    }

    private static void Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{what} is required.");
        }
    }
}
