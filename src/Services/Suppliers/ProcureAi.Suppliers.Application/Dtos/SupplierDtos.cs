namespace ProcureAi.Suppliers.Application.Dtos;

public sealed record SupplierSummaryDto(string Code, string Name, bool Active, int ItemCount);

/// <summary>A supplier with the items it currently offers (discontinued items are left out).</summary>
public sealed record SupplierDto(
    string Code, string Name, string VatNumber, string Email, bool Active, IReadOnlyList<CatalogItemDto> Items);

/// <summary>A catalog line. <c>UnitPriceDisplay</c> is the price as shown to people (<c>R 4 200,00</c>); <c>UnitPriceExclVat</c> is the amount in Rand.</summary>
public sealed record CatalogItemDto(
    string Sku, string Description, string Unit, decimal UnitPriceExclVat, string UnitPriceDisplay, bool Active);

/// <summary>What the internal lookup answers: the supplier and only those of the asked SKUs that exist (active or not).</summary>
public sealed record CatalogItemsDto(string Code, string Name, bool Active, IReadOnlyList<CatalogItemDto> Items);
