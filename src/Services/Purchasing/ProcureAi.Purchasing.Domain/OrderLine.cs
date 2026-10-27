using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Purchasing.Domain;

/// <summary>One line of an order. Received and closed-short quantities change only through the order.</summary>
public sealed class OrderLine
{
    private OrderLine()
    {
    }

    public OrderLine(int lineNo, string sku, string description, int quantity, Money unitPriceExclVat)
    {
        if (lineNo < 1)
        {
            throw new DomainException("A line number must be 1 or more.");
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("A line needs a SKU.");
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
        Sku = sku.Trim();
        Description = description?.Trim() ?? "";
        Quantity = quantity;
        UnitPriceExclVat = unitPriceExclVat;
    }

    public int LineNo { get; private set; }

    public string Sku { get; private set; } = "";

    public string Description { get; private set; } = "";

    public int Quantity { get; private set; }

    public Money UnitPriceExclVat { get; private set; }

    public int Received { get; private set; }

    public int ClosedShort { get; private set; }

    public int Outstanding => Quantity - Received - ClosedShort;

    public Money LineTotalExclVat => UnitPriceExclVat * Quantity;

    internal void Receive(int quantity) => Received += quantity;

    internal int CloseOutstanding()
    {
        var outstanding = Outstanding;
        ClosedShort += outstanding;
        return outstanding;
    }
}
