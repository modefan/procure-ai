using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Tests.Web;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth-test")]
public sealed class AuthTestController(ICurrentUser user) : ControllerBase
{
    [HttpGet("me")]
    [RequirePermission(Permissions.RequestsRead)]
    public IActionResult Me() => Ok(new
    {
        user.UserId,
        user.TenantId,
        user.IsAdminApiKey,
        user.IsServiceAccount,
        user.CallingService,
        user.OnBehalfOfUser,
        Roles = user.Roles.Order().ToArray(),
    });

    [HttpGet("tenant")]
    [RequirePermission(Permissions.RequestsRead)]
    [RequireTenant]
    public IActionResult Tenant() => Ok(new { user.TenantId });

    [HttpPost("approve")]
    [RequirePermission(Permissions.RequestsApprove)]
    public IActionResult Approve() => Ok();

    [HttpGet("log")]
    [RequirePermission(Permissions.RequestsRead)]
    public IActionResult Log([FromServices] ILogger<AuthTestController> logger)
    {
        logger.LogWarning("auth-test-event");
        return Ok();
    }

    [HttpGet("no-attribute")]
    public IActionResult NoAttribute() => Ok();
}
