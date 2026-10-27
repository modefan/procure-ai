using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Application.Mapping;

/// <summary>Domain to DTO. Written by hand: the price and its display text both come from the Money value object.</summary>
public static class SupplierMapper
{
    public static SupplierSummaryDto ToSummary(Supplier supplier) => new(supplier.Code, supplier.Name, supplier.Active, supplier.Items.Count);

    public static SupplierDto ToDto(Supplier supplier) => new(
        supplier.Code, supplier.Name, supplier.VatNumber, supplier.Email, supplier.Active,
        [.. supplier.Items.Where(i => i.Active).Select(ToDto)]);

    public static CatalogItemDto ToDto(CatalogItem item) => new(
        item.Sku, item.Description, item.Unit, item.UnitPriceExclVat.Amount, item.UnitPriceExclVat.ToDisplayString(), item.Active);
}
