using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Suppliers.Domain;

/// <summary>
/// One line of a supplier's catalog: a SKU, what it is, its unit and its price per unit excluding VAT, in Rand. An inactive
/// item is discontinued: it stays in the catalog (old orders refer to it) but is no longer offered.
/// </summary>
public sealed record CatalogItem
{
    public const int MaxSkuLength = 40;
    public const int MaxDescriptionLength = 200;
    public const int MaxUnitLength = 20;

    public CatalogItem(string sku, string description, string unit, Money unitPriceExclVat, bool active)
    {
        Sku = Required(sku, MaxSkuLength, nameof(sku));
        Description = Required(description, MaxDescriptionLength, nameof(description));
        Unit = Required(unit, MaxUnitLength, nameof(unit));
        if (unitPriceExclVat.Currency != Money.ZarCurrency)
        {
            throw new DomainException($"The price of {sku} must be in {Money.ZarCurrency}, got {unitPriceExclVat.Currency}.");
        }

        if (unitPriceExclVat.Amount < 0)
        {
            throw new DomainException($"The price of {sku} cannot be negative.");
        }

        UnitPriceExclVat = unitPriceExclVat;
        Active = active;
    }

    public string Sku { get; }

    public string Description { get; }

    public string Unit { get; }

    public Money UnitPriceExclVat { get; }

    public bool Active { get; }

    private static string Required(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        return value.Length > maxLength ? throw new DomainException($"{name} is longer than {maxLength} characters.") : value;
    }
}
