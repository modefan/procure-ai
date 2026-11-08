using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.Access.Application.Commands;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Queries;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using Wolverine;

namespace ProcureAi.Access.Api.Controllers;

/// <summary>
/// The system-wide on/off switch of the demo (plan 08 Task 12b). Its own controller: it has no tenant, and its read is anonymous (the
/// web app asks before anyone signs in, the gateway asks for every browser call). While the switch is off the gateway answers the
/// browser's calls with 503 and the web app shows its maintenance page.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/system")]
public sealed class SystemController(IMessageBus bus, ICacheInvalidator cache, ILogger<SystemController> logger) : ControllerBase
{
    private const string AvailabilityTag = "access:system:availability";

    /// <summary>Whether the demo is switched on, and the maintenance page's message while it is off. Anyone may ask, signed in or not.</summary>
    [HttpGet("availability", Name = "GetSystemAvailability")]
    [AllowAnonymous]
    [CachedGet(Tags = [AvailabilityTag])]
    [ProducesResponseType<SystemAvailabilityDto>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<SystemAvailabilityDto>> GetAvailability(CancellationToken cancellationToken) =>
        await bus.InvokeAsync<SystemAvailabilityDto>(new GetSystemAvailability(), cancellationToken);

    /// <summary>
    /// Switches the whole demo on or off (sets the state; sending the same body again changes nothing). Only the admin API key: no
    /// user role, not even an admin's, and no service account may do it.
    /// </summary>
    [HttpPut("availability", Name = "SetSystemAvailability")]
    [Authorize(Policy = AdminApiKeyPolicy.Name)]
    [ProducesResponseType<SystemAvailabilityDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SystemAvailabilityDto>> SetAvailability(SetSystemAvailabilityBody body, CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<SystemAvailabilityDto>(new SetSystemAvailability(body.Enabled!.Value, body.Message), cancellationToken);
        logger.LogWarning("The demo was switched {State}", result.Enabled ? "on" : "off");
        try
        {
            await cache.RemoveByTagAsync([AvailabilityTag], CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for {Tag}; the cached read expires on its own", AvailabilityTag);
        }

        return result;
    }
}

/// <summary>The switch: <c>enabled</c> (required) and an optional message the maintenance page shows while the demo is off.</summary>
public sealed record SetSystemAvailabilityBody([Required] bool? Enabled, [MaxLength(SystemSettings.MaxMessageLength)] string? Message = null);
