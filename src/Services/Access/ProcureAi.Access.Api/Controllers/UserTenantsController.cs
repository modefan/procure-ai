using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Queries;
using ProcureAi.BuildingBlocks.Web.Auth;
using Wolverine;

namespace ProcureAi.Access.Api.Controllers;

/// <summary>
/// The one lookup across tenants: every tenant a user belongs to. Its own controller because it has no tenant of its own, so it
/// does not carry <c>[RequireTenant]</c> like <see cref="AccessController"/>. Service account or admin API key only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/access")]
public sealed class UserTenantsController(IMessageBus bus) : ControllerBase
{
    /// <summary>
    /// Internal: every tenant in which <c>userId</c> has a user row, with the tenant's name (for the tenant switcher). Deliberately
    /// across tenants; empty for an unknown user. Not cached: the answer must follow a membership change at once.
    /// </summary>
    [HttpGet("internal/users/{userId}/tenants", Name = "ListUserTenants")]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<IReadOnlyList<TenantDto>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<TenantDto>>> ListUserTenants(
        [MaxLength(AccessLimits.MaxCodeLength)] string userId, CancellationToken cancellationToken) =>
        Ok(await bus.InvokeAsync<IReadOnlyList<TenantDto>>(new ListUserTenants(userId), cancellationToken));
}
