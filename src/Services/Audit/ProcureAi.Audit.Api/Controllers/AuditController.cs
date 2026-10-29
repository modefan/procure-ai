using Asp.Versioning;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Application.Queries;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using Wolverine;

namespace ProcureAi.Audit.Api.Controllers;

/// <summary>The business audit of the caller tenant: what happened to a purchase request, by whom, when.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
[RequireTenant]
public sealed class AuditController(IMessageBus bus, ICurrentUser user, IPermissionProvider permissions) : ControllerBase
{
    /// <summary>
    /// The business events of a purchase request, oldest first, each with a one-line summary (names from Access; user IDs when
    /// Access cannot answer). 404 when the tenant has no events for it.
    /// </summary>
    [HttpGet("requests/{requestId:guid}/history", Name = "GetRequestHistory")]
    [CachedGet]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<IReadOnlyList<BusinessEventDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<BusinessEventDto>>> GetRequestHistory(Guid requestId, CancellationToken cancellationToken) =>
        Ok(await bus.InvokeAsync<IReadOnlyList<BusinessEventDto>>(new GetRequestHistory(requestId), cancellationToken));

    /// <summary>
    /// The latest business events of the tenant (1 to 50, default 20), newest first, for the dashboard. Budget events are listed only
    /// for callers with <c>budgets.manage</c>, invoice and payment events only for finance (<c>invoices.release-payment</c>) and the
    /// admin key, because the request history is tenant-wide but budgets and invoices are scoped to cost centers.
    /// </summary>
    [HttpGet("activity", Name = "GetActivity")]
    [CachedGet]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<IReadOnlyList<ActivityDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ActivityDto>>> GetActivity(
        [FromQuery, Range(1, 50)] int limit = 20, CancellationToken cancellationToken = default)
    {
        var granted = await permissions.GetPermissionsAsync(user, cancellationToken);
        return Ok(await bus.InvokeAsync<IReadOnlyList<ActivityDto>>(
            new GetActivity(limit, granted.Contains(Permissions.BudgetsManage), granted.Contains(Permissions.InvoicesReleasePayment)),
            cancellationToken));
    }
}
