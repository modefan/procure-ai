using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Application.Mapping;

namespace ProcureAi.Suppliers.Application.Queries;

/// <summary>The tenant's suppliers by code; <c>Search</c> narrows to those whose code or name contains it.</summary>
public sealed record ListSuppliers(string? Search, int Page, int PageSize);

public static class ListSuppliersHandler
{
    public static async Task<PagedResult<SupplierSummaryDto>> Handle(
        ListSuppliers query, ISupplierStore store, CancellationToken cancellationToken)
    {
        var (items, total) = await store.ListAsync(
            SearchText.Normalise(query.Search), (query.Page - 1) * query.PageSize, query.PageSize, cancellationToken);
        return new PagedResult<SupplierSummaryDto>([.. items.Select(SupplierMapper.ToSummary)], query.Page, query.PageSize, total);
    }
}

internal static class SearchText
{
    public static string? Normalise(string? search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();
}
