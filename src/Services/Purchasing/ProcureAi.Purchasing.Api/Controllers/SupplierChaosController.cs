using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.Purchasing.Application.Abstractions;

namespace ProcureAi.Purchasing.Api.Controllers;

/// <summary>The supplier's new chaos mode.</summary>
public sealed record SupplierChaosBody([Required, EnumDataType(typeof(ChaosMode))] ChaosMode Mode);

/// <summary>
/// "Break the supplier API" (plan 06 ruling 11): sets the simulated supplier's chaos mode (Normal, Slow, Failing, Down) through the simulator,
/// which restores Normal by itself after <c>Simulators:Chaos:AutoRestoreAfter</c> (5 minutes); <c>restoresAt</c> says when. The supplier is
/// shared by every tenant: this is a demo switch for admins (<c>admin.system</c>), and the circuit breaker on the system status screen shows
/// the effect. 503 when the simulator does not answer.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/purchasing/admin/supplier-chaos")]
public sealed class SupplierChaosController(ISupplierChaos chaos, ICacheInvalidator cache, ILogger<SupplierChaosController> logger) : ControllerBase
{
    private const string CacheTag = "purchasing:supplier-chaos";

    /// <summary>The supplier's chaos mode now, and when it restores itself.</summary>
    [HttpGet(Name = "GetSupplierChaos")]
    [CachedGet(Tags = [CacheTag])]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<ChaosDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChaosDto>> Get(CancellationToken cancellationToken) => await chaos.GetAsync(cancellationToken);

    /// <summary>Sets the supplier's chaos mode. Setting the mode it already has keeps the running countdown.</summary>
    [HttpPut(Name = "SetSupplierChaos")]
    [Idempotent]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<ChaosDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChaosDto>> Set(SupplierChaosBody body, CancellationToken cancellationToken)
    {
        var result = await chaos.SetAsync(body.Mode, cancellationToken);
        logger.LogInformation("Supplier chaos set to {Mode}, restores at {RestoresAt}", result.Mode, result.RestoresAt);
        try
        {
            await cache.RemoveByTagAsync([CacheTag], CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not drop the cached supplier chaos state; it expires within a second");
        }

        return result;
    }
}
