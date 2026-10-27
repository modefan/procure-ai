using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Application.Mapping;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Application.Queries;

/// <summary>Internal lookup for other services (Requests validates and prices lines with it): the asked SKUs that exist, active or not.</summary>
public sealed record GetCatalogItems(string Code, IReadOnlyList<string> Skus);

public static class GetCatalogItemsHandler
{
    public static async Task<CatalogItemsDto> Handle(GetCatalogItems query, ISupplierStore store, CancellationToken cancellationToken)
    {
        if (query.Skus.Count > SuppliersLimits.MaxLookupSkus)
        {
            throw new DomainException($"At most {SuppliersLimits.MaxLookupSkus} SKUs can be looked up at once.");
        }

        var supplier = await SupplierLookup.RequireAsync(store, query.Code, cancellationToken);
        var found = query.Skus.Distinct(StringComparer.Ordinal)
            .Select(supplier.Find).OfType<CatalogItem>()
            .Select(SupplierMapper.ToDto).ToList();
        return new CatalogItemsDto(supplier.Code, supplier.Name, supplier.Active, found);
    }
}
