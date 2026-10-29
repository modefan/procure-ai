using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Info;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>What the assistant screen needs to know about the model before the first question.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assistant")]
[RequireTenant]
public sealed class AssistantInfoController(IOptions<AiOptions> ai, IOptions<AssistantOptions> assistant) : ControllerBase
{
    /// <summary>The provider and model that answer, whether it is the demo model (no AI account), and the daily question quota.</summary>
    [HttpGet("info", Name = "GetAssistantInfo")]
    [CachedGet]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<AssistantInfo>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<AssistantInfo> Info() => AssistantInfo.From(ai.Value, assistant.Value);
}
