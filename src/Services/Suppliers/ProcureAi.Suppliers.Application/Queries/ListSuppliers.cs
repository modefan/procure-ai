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
        ListSuppliers query, ISupplierStore store, IOptions<PagingOptions> paging, CancellationToken cancellationToken)
    {
        PagingRules.Validate(query.Page, query.PageSize, paging.Value);
        var (items, total) = await store.ListAsync(
            PagingRules.Normalise(query.Search), (query.Page - 1) * query.PageSize, query.PageSize, cancellationToken);
        return new PagedResult<SupplierSummaryDto>([.. items.Select(SupplierMapper.ToSummary)], query.Page, query.PageSize, total);
    }
}

internal static class PagingRules
{
    public static void Validate(int page, int pageSize, PagingOptions options)
    {
        if (page < 1)
        {
            throw new DomainException("Page must be 1 or more.");
        }

        if (pageSize < 1 || pageSize > options.MaxPageSize)
        {
            throw new DomainException($"Page size must be between 1 and {options.MaxPageSize}.");
        }
    }

    public static string? Normalise(string? search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();
}
