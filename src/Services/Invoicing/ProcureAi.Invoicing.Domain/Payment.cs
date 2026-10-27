using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>The quantity of one order line a payment covers, at the order price.</summary>
public sealed record PaymentLine(int LineNo, string Sku, int Quantity, Money AmountExclVat);

/// <summary>One payment released by finance against an invoice, and its export to the ERP.</summary>
public sealed class Payment
{
    private readonly List<PaymentLine> _lines = [];

    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public PaymentNumber Number { get; private set; }

    public IReadOnlyList<PaymentLine> Lines => _lines.AsReadOnly();

    public Money TotalExclVat { get; private set; }

    public Money Vat { get; private set; }

    public Money TotalInclVat { get; private set; }

    public string ReleasedBy { get; private set; } = "";

    public DateTimeOffset ReleasedAt { get; private set; }

    public ExportStatus ExportStatus { get; private set; }

    public string? ErpReference { get; private set; }

    /// <summary>How many times the ERP could not take the payment.</summary>
    public int ExportAttempts { get; private set; }

    public string? LastExportError { get; private set; }

    internal static Payment Create(PaymentPlan plan, PaymentNumber number, string releasedBy, DateTimeOffset now)
    {
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            Number = number,
            TotalExclVat = plan.TotalExclVat,
            Vat = plan.Vat,
            TotalInclVat = plan.TotalInclVat,
            ReleasedBy = releasedBy,
            ReleasedAt = now,
            ExportStatus = ExportStatus.Pending,
        };
        payment._lines.AddRange(plan.Lines.Select(l => new PaymentLine(l.LineNo, l.Sku, l.Quantity, l.AmountExclVat)));
        return payment;
    }

    /// <summary>The ERP could not be reached or refused the payment; the error is kept for people to see.</summary>
    public void RecordExportFailure(string error)
    {
        if (ExportStatus == ExportStatus.Exported)
        {
            throw new DomainException("The payment was already exported.");
        }

        ExportAttempts++;
        LastExportError = error;
    }

    /// <summary>The ERP took the payment. True the first time; repeating it with the reference already stored changes nothing.</summary>
    public bool MarkExported(string erpReference)
    {
        if (string.IsNullOrWhiteSpace(erpReference))
        {
            throw new DomainException("The ERP reference is required.");
        }

        if (ExportStatus == ExportStatus.Exported)
        {
            return false;
        }

        ExportStatus = ExportStatus.Exported;
        ErpReference = erpReference;
        LastExportError = null;
        return true;
    }
}

/// <summary>One line of a <see cref="PaymentPlan"/>.</summary>
public sealed record PaymentPlanLine(int LineNo, string Sku, int Quantity, Money AmountExclVat);

/// <summary>
/// What a payment would pay, worked out against the invoice and its order and not yet applied. Amounts are at the order price;
/// VAT is worked out on the total of the payment.
/// </summary>
public sealed class PaymentPlan
{
    internal PaymentPlan(Invoice invoice, OrderView order, IReadOnlyList<PaymentPlanLine> lines)
    {
        Invoice = invoice;
        Order = order;
        Lines = lines;
        TotalExclVat = lines.Aggregate(Money.Zero, (sum, l) => sum + l.AmountExclVat);
        Vat = TotalExclVat.Vat();
        TotalInclVat = TotalExclVat + Vat;
    }

    internal Invoice Invoice { get; }

    internal OrderView Order { get; }

    public IReadOnlyList<PaymentPlanLine> Lines { get; }

    public Money TotalExclVat { get; }

    public Money Vat { get; }

    public Money TotalInclVat { get; }
}
