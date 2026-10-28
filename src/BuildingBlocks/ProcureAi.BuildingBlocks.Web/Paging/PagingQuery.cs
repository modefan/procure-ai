using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Web.Paging;

/// <summary>
/// The <c>page</c> and <c>pageSize</c> query parameters of every paged list, validated against <see cref="PagingOptions"/>
/// (section <c>Paging</c>): <c>Page</c> 1 or more, <c>PageSize</c> from 1 to <c>MaxPageSize</c>, default <c>DefaultPageSize</c>.
/// An action takes <c>[FromQuery] PagingQuery paging</c>; a violation answers 400 problem details naming the field
/// (<c>page</c> or <c>pageSize</c>) before the action runs. After validation <see cref="Size"/> is the page size to use.
/// </summary>
public sealed class PagingQuery : IValidatableObject
{
    [FromQuery(Name = "page")]
    [DefaultValue(1)]
    public int Page { get; set; } = 1;

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    private int _size;

    /// <summary>
    /// The page size to use: what the caller asked for, else the configured default. Known after validation. A method, not a
    /// property, so it is neither bound from the query string nor listed as a parameter in the OpenAPI document.
    /// </summary>
    public int Size() => _size;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var options = validationContext.GetRequiredService<IOptions<PagingOptions>>().Value;
        _size = PageSize ?? options.DefaultPageSize;
        var results = new List<ValidationResult>();
        if (Page < 1)
        {
            results.Add(new ValidationResult("Page must be 1 or more.", ["page"]));
        }

        if (_size < 1 || _size > options.MaxPageSize)
        {
            results.Add(new ValidationResult($"Page size must be between 1 and {options.MaxPageSize}.", ["pageSize"]));
        }

        return results;
    }
}
