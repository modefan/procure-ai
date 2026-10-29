using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Paging;
using ProcureAi.Suppliers.Application;
using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Application.Queries;
using Wolverine;

namespace ProcureAi.Suppliers.Api.Controllers;

/// <summary>
/// The supplier catalog of the caller tenant, in Rand. Read only: the catalog is loaded by the migrate step. Each tenant has its own
/// suppliers, items and prices (the same code in two tenants is two suppliers).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/suppliers")]
[RequireTenant]
public sealed class SuppliersController(IMessageBus bus) : ControllerBase
{
    /// <summary>The tenant's suppliers by code; <c>search</c> narrows to those whose code or name contains it.</summary>
    [HttpGet(Name = "ListSuppliers")]
    [CachedGet]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<SupplierSummaryDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<SupplierSummaryDto>>> List(
        [FromQuery, MaxLength(SuppliersLimits.MaxSearchLength)] string? search,
        [FromQuery] PagingQuery paging,
        CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<SupplierSummaryDto>>(new ListSuppliers(search, paging.Page, paging.Size()), cancellationToken);
    }

    /// <summary>One supplier with the items it currently offers.</summary>
    [HttpGet("{code}", Name = "GetSupplier")]
    [CachedGet]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierDto>> Get(
        [MaxLength(SuppliersLimits.MaxCodeLength)] string code, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<SupplierDto>(new GetSupplier(code), cancellationToken);

    /// <summary>The items of one supplier by SKU, active or not (the <c>active</c> flag says which are still offered); <c>search</c> matches SKU or description.</summary>
    [HttpGet("{code}/items", Name = "SearchCatalog")]
    [CachedGet]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<CatalogItemDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<CatalogItemDto>>> Items(
        [MaxLength(SuppliersLimits.MaxCodeLength)] string code,
        [FromQuery, MaxLength(SuppliersLimits.MaxSearchLength)] string? search,
        [FromQuery] PagingQuery paging,
        CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<CatalogItemDto>>(new SearchCatalog(code, search, paging.Page, paging.Size()), cancellationToken);
    }

    /// <summary>
    /// Internal: the asked SKUs of a supplier of the tenant in <c>X-Tenant-Id</c> (at most 100, comma separated), only those that
    /// exist, active or not. For other services (service account or admin API key); Requests prices and checks its lines with it.
    /// </summary>
    [HttpGet("internal/{code}/items", Name = "GetCatalogItems")]
    [CachedGet]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<CatalogItemsDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CatalogItemsDto>> GetItems(
        [MaxLength(SuppliersLimits.MaxCodeLength)] string code,
        [FromQuery, Required] string skus,
        CancellationToken cancellationToken)
    {
        var parsed = skus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (parsed.Count == 0)
        {
            ModelState.AddModelError(nameof(skus), "At least one SKU is required.");
        }

        if (parsed.Count > SuppliersLimits.MaxLookupSkus)
        {
            ModelState.AddModelError(nameof(skus), $"At most {SuppliersLimits.MaxLookupSkus} SKUs can be looked up at once.");
        }

        if (parsed.Any(s => s.Length > SuppliersLimits.MaxSkuLength))
        {
            ModelState.AddModelError(nameof(skus), $"A SKU is at most {SuppliersLimits.MaxSkuLength} characters.");
        }

        return ModelState.IsValid
            ? await bus.InvokeAsync<CatalogItemsDto>(new GetCatalogItems(code, parsed), cancellationToken)
            : ValidationProblem(ModelState);
    }
}
