using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.Access.Application.Commands;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Queries;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using Wolverine;

namespace ProcureAi.Access.Api.Controllers;

/// <summary>
/// The permission matrix and the users of the caller tenant, and the lookups other services make. Changes are checked
/// against the version in <c>If-Match</c>.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/access")]
[RequireTenant]
public sealed class AccessController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ILogger<AccessController> logger) : ControllerBase
{
    // Read tags and the invalidation after a write both format these templates with CacheTags.Format.
    private const string MatrixTagTemplate = "access:{tenant}:matrix";
    private const string UsersTagTemplate = "access:{tenant}:users";

    [HttpGet("matrix", Name = "GetMatrix")]
    [CachedGet(Tags = [MatrixTagTemplate])]
    [RequirePermission(Permissions.AdminAuthorization)]
    [ProducesResponseType<MatrixDto>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<MatrixDto>> GetMatrix(CancellationToken cancellationToken)
    {
        var matrix = await bus.InvokeAsync<MatrixDto>(new GetMatrix(), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(matrix.Version);
        return matrix;
    }

    /// <summary>Replaces the permissions of one role. Send the matrix version in <c>If-Match</c>.</summary>
    [HttpPut("matrix/roles/{role}", Name = "SetRolePermissions")]
    [RequirePermission(Permissions.AdminAuthorization)]
    [RequireIfMatch]
    [ProducesResponseType<MatrixDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<MatrixDto>> SetRolePermissions(
        [MaxLength(AccessLimits.MaxCodeLength)] string role, SetRolePermissionsBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var matrix = await bus.InvokeAsync<MatrixDto>(new SetRolePermissions(role, body.Permissions, version), cancellationToken);
        await InvalidateAsync(MatrixTagTemplate);
        Response.Headers.ETag = VersionETag.Format(matrix.Version);
        return matrix;
    }

    [HttpGet("users", Name = "ListUsers")]
    [CachedGet(Tags = [UsersTagTemplate])]
    [RequirePermission(Permissions.AdminUsers)]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> ListUsers(CancellationToken cancellationToken) =>
        Ok(await bus.InvokeAsync<IReadOnlyList<UserDto>>(new ListUsers(), cancellationToken));

    /// <summary>Sets the approval limit (Rand) of a user. Send the user version (<c>version</c> of the list) in <c>If-Match</c>.</summary>
    [HttpPut("users/{userId}/approval-limit", Name = "SetApprovalLimit")]
    [RequirePermission(Permissions.AdminUsers)]
    [RequireIfMatch]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<UserDto>> SetApprovalLimit(
        [MaxLength(AccessLimits.MaxCodeLength)] string userId, SetApprovalLimitBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var updated = await bus.InvokeAsync<UserDto>(new SetApprovalLimit(userId, body.Amount, version), cancellationToken);
        await InvalidateAsync(UsersTagTemplate);
        Response.Headers.ETag = VersionETag.Format(updated.Version);
        return updated;
    }

    // The internal lookups below are deliberately NOT [CachedGet] (an exception to the "1 s cache on every GET" rule, final
    // review I-3). A write sends AccessChanged through the outbox inside InvokeAsync, before this controller evicts its cache,
    // so a consumer reacting to it could get the pre-change answer from the micro-cache and keep it under its new cache
    // generation. Server-side coalescing alone has the same flaw (a call arriving after the commit can join a run that read
    // the old state). The callers' AccessClient already caches and coalesces these lookups per tenant generation.

    /// <summary>Internal: what a user may do, given the roles of their token. For other services (service account or admin API key).</summary>
    [HttpGet("internal/effective", Name = "GetEffectiveAccess")]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<EffectiveAccessDto>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<EffectiveAccessDto>> GetEffective(
        [FromQuery, Required, MaxLength(AccessLimits.MaxCodeLength)] string userId,
        [FromQuery] string[] roles,
        CancellationToken cancellationToken)
    {
        // Roles come as repeated parameters (roles=a&roles=b) or comma separated (roles=a,b).
        var parsed = roles.SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToList();
        return await bus.InvokeAsync<EffectiveAccessDto>(new GetEffectiveAccess(userId, parsed), cancellationToken);
    }

    /// <summary>Internal: the manager, department head and finance approvers of a cost center.</summary>
    [HttpGet("internal/cost-centers/{code}/approvers", Name = "GetCostCenterApprovers")]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<CostCenterApproversDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CostCenterApproversDto>> GetApprovers(
        [MaxLength(AccessLimits.MaxCodeLength)] string code, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<CostCenterApproversDto>(new GetCostCenterApprovers(code), cancellationToken);

    /// <summary>Internal: the display name of a user of the tenant (audit history, inboxes). 404 when the tenant has no such user.</summary>
    [HttpGet("internal/users/{userId}", Name = "GetUserName")]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<UserNameDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserNameDto>> GetUserName(
        [MaxLength(AccessLimits.MaxCodeLength)] string userId, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<UserNameDto>(new GetUserName(userId), cancellationToken);

    /// <summary>
    /// Internal: a user who signed up in the identity provider joins the demo company (organization, requester role, sandbox
    /// cost center, tenant user). 201 when this call added them, 200 when they were already in. Called by the gateway for the
    /// signed-in user only: the user in the body must be the one in <c>X-On-Behalf-Of-User</c>, otherwise 403. Email and name
    /// are read from the identity provider, never taken from the caller. 409 when the user already belongs to another company.
    /// </summary>
    [HttpPost("internal/registrations", Name = "RegisterDemoUser")]
    [Idempotent]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<RegistrationDto>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<RegistrationDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegistrationDto>> RegisterDemoUser(RegisterDemoUserBody body, CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<RegistrationDto>(new RegisterDemoUser(body.UserId), cancellationToken);
        if (!result.Joined)
        {
            return Ok(result);
        }

        await InvalidateAsync(UsersTagTemplate);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Drops the cached reads of the tenant after a write, so no read after it sees the old state. Runs after the commit,
    /// so it ignores the cancellation of the client, and a failure here is logged, never raised: the cached entries
    /// expire by themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync(string changedTemplate)
    {
        var tenant = CacheTags.Tenant(user);
        try
        {
            string?[] tags = [CacheTags.Format(changedTemplate, tenant)];
            await cache.RemoveByTagAsync(tags.OfType<string>(), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for {Tag}; cached reads expire on their own", changedTemplate);
        }
    }
}
