using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Application.Mapping;

namespace ProcureAi.Suppliers.Application.Queries;

/// <summary>
/// The items of one supplier by SKU, active or not (the <c>Active</c> flag says which are still offered); <c>Search</c> narrows to
/// those whose SKU or description contains it, case-insensitive.
/// </summary>
public sealed record SearchCatalog(string Code, string? Search, int Page, int PageSize);

public static class SearchCatalogHandler
{
    public static async Task<PagedResult<CatalogItemDto>> Handle(
        SearchCatalog query, ISupplierStore store, IOptions<PagingOptions> paging, CancellationToken cancellationToken)
    {
        PagingRules.Validate(query.Page, query.PageSize, paging.Value);
        var supplier = await SupplierLookup.RequireAsync(store, query.Code, cancellationToken);
        var search = PagingRules.Normalise(query.Search);
        var matches = supplier.Items
            .Where(i => search is null
                || i.Sku.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Sku, StringComparer.Ordinal)
            .ToList();
        var page = matches.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(SupplierMapper.ToDto).ToList();
        return new PagedResult<CatalogItemDto>(page, query.Page, query.PageSize, matches.Count);
    }
}
