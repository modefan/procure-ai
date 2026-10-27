using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>Configuration section <c>Paging</c> of a service with paged lists.</summary>
public sealed class PagingOptions
{
    public const string SectionName = "Paging";

    [Range(1, 1000)]
    public int MaxPageSize { get; init; } = 100;

    /// <summary>Page size when the caller does not ask for one.</summary>
    [Range(1, 1000)]
    public int DefaultPageSize { get; init; } = 25;
}

/// <summary>One page of a list: the items, the page number (from 1), the page size and the total number of items.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
