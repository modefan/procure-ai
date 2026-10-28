using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Paging;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using ProcureAi.Requests.Application;
using ProcureAi.Requests.Application.Commands;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Queries;
using ProcureAi.Requests.Domain;
using Wolverine;

namespace ProcureAi.Requests.Api.Controllers;

/// <summary>Purchase requests of the caller's tenant. Writes are checked against the version in <c>If-Match</c>.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/requests")]
[RequireTenant]
public sealed class RequestsController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ICallerContext caller, ILogger<RequestsController> logger) : ControllerBase
{
    [HttpPost(Name = "CreateRequest")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsCreate)]
    [ProducesResponseType<RequestDto>(StatusCodes.Status201Created, "application/json")]
    public async Task<ActionResult<RequestDto>> Create(CreateRequestBody body, CancellationToken cancellationToken)
    {
        var created = await bus.InvokeAsync<RequestDto>(body.ToCommand(), cancellationToken);
        await InvalidateAsync(created.Id);
        Response.Headers.ETag = VersionETag.Format(created.Version);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}", Name = "GetRequest")]
    [CachedGet(Tags = [RequestCacheTag.ItemTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var request = await bus.InvokeAsync<RequestDto>(new GetRequest(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(request.Version);
        return request;
    }

    /// <summary>
    /// The tenant's requests, newest first. <c>status</c> is one status or a group (<c>open</c>: not finished,
    /// <c>closed</c>: completed, rejected or cancelled); <c>mine=true</c> limits the list to requests the caller made.
    /// </summary>
    [HttpGet(Name = "ListRequests")]
    [CachedGet(Tags = [RequestCacheTag.ListTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<RequestDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<RequestDto>>> List(
        [FromQuery] PagingQuery paging,
        [FromQuery] RequestStatusFilter? status = null,
        [FromQuery] bool mine = false,
        CancellationToken cancellationToken = default)
    {
        // "mine" is the requests the caller made (for a service or the admin key acting for a user, that user).
        var requester = mine ? caller.UserId : null;
        return await bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(paging.Page, paging.Size(), status, requester), cancellationToken);
    }

    [HttpPut("{id:guid}", Name = "UpdateRequest")]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<RequestDto>> Update(
        Guid id, UpdateRequestBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var updated = await bus.InvokeAsync<RequestDto>(body.ToCommand(id, version), cancellationToken);
        await InvalidateAsync(id);
        Response.Headers.ETag = VersionETag.Format(updated.Version);
        return updated;
    }

    /// <summary>Submits a draft for approval. Returns the request as it is now, with its new ETag.</summary>
    [HttpPost("{id:guid}/submit", Name = "SubmitRequest")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status202Accepted, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<RequestDto>> Submit(Guid id, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        await bus.InvokeAsync(new SubmitRequest(id, version), cancellationToken);
        await InvalidateAsync(id);
        // The write has committed: a client that disconnects now must not turn it into a failure (its retry would run it again).
        var submitted = await bus.InvokeAsync<RequestDto>(new GetRequest(id), CancellationToken.None);
        Response.Headers.ETag = VersionETag.Format(submitted.Version);
        return AcceptedAtAction(nameof(Get), new { id }, submitted);
    }

    /// <summary>
    /// Cancels the caller's own request (a draft, a submitted or an approved one). A submitted or approved request publishes
    /// <c>RequestCancelled</c>: the approval saga stops and the budget is released. 403 for anyone but the requester, 412 when
    /// the request changed since it was read, 422 when it can no longer be cancelled (rejected or cancelled).
    /// </summary>
    [HttpPost("{id:guid}/cancel", Name = "CancelRequest")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<RequestDto>> Cancel(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelRequestBody? body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var cancelled = await bus.InvokeAsync<RequestDto>((body ?? new CancelRequestBody(null)).ToCommand(id, version), cancellationToken);
        await InvalidateAsync(id);
        Response.Headers.ETag = VersionETag.Format(cancelled.Version);
        return cancelled;
    }

    /// <summary>
    /// Drops the cached reads of this request and of the tenant list, so no read after a write sees the old state.
    /// Runs after the commit, so it ignores the client's cancellation, and a failure here is logged, never raised:
    /// the cached entries expire by themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync(Guid id)
    {
        try
        {
            await cache.RemoveByTagAsync(RequestCacheTag.Of(user.TenantId, id), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for request {RequestId}; cached reads expire on their own", id);
        }
    }
}
