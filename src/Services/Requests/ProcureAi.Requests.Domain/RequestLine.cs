using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Requests.Domain;

public sealed record RequestLine(int LineNo, string Sku, string Description, int Quantity, Money UnitPriceExclVat)
{
    public Money TotalExclVat => UnitPriceExclVat * Quantity;
}
