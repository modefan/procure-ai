using Asp.Versioning;
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
public sealed class AuditController(IMessageBus bus) : ControllerBase
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
}
