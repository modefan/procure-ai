using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Requests.Application;

/// <summary>Configuration section <c>Paging</c>.</summary>
public sealed class PagingOptions
{
    public const string SectionName = "Paging";

    [Range(1, 1000)]
    public int MaxPageSize { get; init; } = 100;

    /// <summary>Page size when the caller does not ask for one.</summary>
    [Range(1, 1000)]
    public int DefaultPageSize { get; init; } = 25;
}
