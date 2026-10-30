using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Tests.Diagnostics;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/devpanel")]
public sealed class DevPanelController : ControllerBase
{
    [HttpGet("one-policy")]
    [RequirePermission(Permissions.RequestsRead)]
    public IActionResult OnePolicy() => Ok();

    [HttpGet("two-policies")]
    [RequirePermission(Permissions.RequestsRead)]
    [RequirePermission(Permissions.RequestsCreate)]
    public IActionResult TwoPolicies() => Ok();
}
