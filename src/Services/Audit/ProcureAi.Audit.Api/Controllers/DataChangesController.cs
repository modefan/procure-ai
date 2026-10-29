using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Application.Queries;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Paging;
using Wolverine;

namespace ProcureAi.Audit.Api.Controllers;

/// <summary>
/// The data change audit: every change of the services' captured tables (Debezium), with field-level old and new values and who
/// made it (the app user of the interceptor row of the same transaction), or flagged "changed outside the app".
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
public sealed class DataChangesController(IMessageBus bus) : ControllerBase
{
    /// <summary>
    /// The data changes of a purchase request of the caller tenant across services (request, lines, budget reservation), oldest
    /// first. 404 when the tenant has no change for it.
    /// </summary>
    [HttpGet("requests/{requestId:guid}/data-changes", Name = "GetRequestDataChanges")]
    [CachedGet]
    [RequireTenant]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<DataChangeDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<DataChangeDto>>> GetRequestDataChanges(
        Guid requestId, [FromQuery] PagingQuery paging, CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<DataChangeDto>>(
            new GetRequestDataChanges(requestId, paging.Page, paging.Size()), cancellationToken);
    }

    /// <summary>
    /// The data-change log, newest first, filtered by database (service), table, user, "outside the app only" and commit time
    /// (<paramref name="from"/> inclusive, <paramref name="to"/> exclusive). A user sees their own tenant; the admin API key without
    /// <c>X-Tenant-Id</c> sees every tenant and the changes no tenant can be told for.
    /// </summary>
    [HttpGet("data-changes", Name = "SearchDataChanges")]
    [CachedGet]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<PagedResult<DataChangeDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<DataChangeDto>>> SearchDataChanges(
        [FromQuery] PagingQuery paging,
        [FromQuery] string? database = null, [FromQuery] string? table = null, [FromQuery] string? userId = null,
        [FromQuery] bool outsideOnly = false, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<DataChangeDto>>(
            new SearchDataChanges(database, table, userId, outsideOnly, from, to, paging.Page, paging.Size()), cancellationToken);
    }
}
