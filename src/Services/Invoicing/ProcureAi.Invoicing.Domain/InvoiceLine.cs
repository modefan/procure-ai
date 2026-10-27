using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>One line of an invoice, as the supplier (or finance, typing it in) stated it.</summary>
public sealed class InvoiceLine
{
    private InvoiceLine()
    {
    }

    public InvoiceLine(int lineNo, string sku, string description, int quantity, Money unitPriceExclVat)
    {
        if (lineNo < 1)
        {
            throw new DomainException("A line number must be 1 or more.");
        }

        if (quantity < 1)
        {
            throw new DomainException($"Line {lineNo}: the quantity must be positive.");
        }

        if (unitPriceExclVat.Currency != Money.ZarCurrency)
        {
            throw new DomainException($"Line {lineNo}: only {Money.ZarCurrency} is supported.");
        }

        if (unitPriceExclVat.Amount < 0)
        {
            throw new DomainException($"Line {lineNo}: the price cannot be negative.");
        }

        LineNo = lineNo;
        Sku = sku?.Trim() ?? "";
        Description = description?.Trim() ?? "";
        Quantity = quantity;
        UnitPriceExclVat = unitPriceExclVat;
    }

    public int LineNo { get; private set; }

    /// <summary>Empty when the invoice did not state a SKU; it is then taken from the order line it is matched to.</summary>
    public string Sku { get; private set; } = "";

    public string Description { get; private set; } = "";

    public int Quantity { get; private set; }

    public Money UnitPriceExclVat { get; private set; }

    /// <summary>How much of this line has been paid.</summary>
    public int Paid { get; private set; }

    public Money LineTotalExclVat => UnitPriceExclVat * Quantity;

    internal void AddPaid(int quantity) => Paid += quantity;

    internal void TakeSkuFrom(OrderViewLine orderLine)
    {
        if (Sku.Length == 0)
        {
            Sku = orderLine.Sku;
        }
    }
}
