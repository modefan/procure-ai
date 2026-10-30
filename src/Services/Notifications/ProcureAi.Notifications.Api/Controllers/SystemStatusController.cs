using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Status;
using ProcureAi.Notifications.Application.SystemStatus;

namespace ProcureAi.Notifications.Api.Controllers;

/// <summary>
/// The system status screen (plan 06 rulings 9 and 10), without calling any service: every instance writes a snapshot to Redis every
/// <c>Status:PublishInterval</c> (TTL <c>Status:Ttl</c>), and this merges them. A service of <c>Status:ExpectedServices</c> without a snapshot is
/// Down; per breaker the worst state of any instance wins. Business counters and alerts are the caller's tenant's plus the system-wide ones:
/// an admin never sees another tenant's numbers.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications/system-status")]
[RequireTenant]
public sealed class SystemStatusController(SystemStatusService status, IOptions<StatusOptions> options, ICurrentUser user) : ControllerBase
{
    [HttpGet(Name = "GetSystemStatus")]
    [CachedGet]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<SystemStatusDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SystemStatusDto>> Get(CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId ?? throw new ForbiddenException("The caller has no tenant.");
        return await status.GetAsync(tenantId, options.Value.ExpectedServices, cancellationToken);
    }
}
