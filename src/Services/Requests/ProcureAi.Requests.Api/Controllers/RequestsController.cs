using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
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
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, IOptions<PagingOptions> paging,
    ILogger<RequestsController> logger) : ControllerBase
{
    // Read tags and the invalidation after a write both format these templates with CacheTags.Format.
    private const string ItemTagTemplate = "requests:{tenant}:{id}";
    private const string ListTagTemplate = "requests:{tenant}:list";

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
    [CachedGet(Tags = [ItemTagTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var request = await bus.InvokeAsync<RequestDto>(new GetRequest(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(request.Version);
        return request;
    }

    [HttpGet(Name = "ListRequests")]
    [CachedGet(Tags = [ListTagTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<RequestDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<RequestDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        // Bad query input is a 400 validation problem (like a bad body), not a 422 business-rule failure.
        // The limit comes from configuration (Paging), so it cannot be an attribute.
        var size = pageSize ?? paging.Value.DefaultPageSize;
        if (page < 1)
        {
            ModelState.AddModelError(nameof(page), "Page must be 1 or more.");
        }

        if (size < 1 || size > paging.Value.MaxPageSize)
        {
            ModelState.AddModelError(nameof(pageSize), $"Page size must be between 1 and {paging.Value.MaxPageSize}.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return await bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(page, size, status), cancellationToken);
    }

    [HttpPut("{id:guid}", Name = "UpdateRequest")]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK, "application/json")]
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
    /// Drops the cached reads of this request and of the tenant list, so no read after a write sees the old state.
    /// Runs after the commit, so it ignores the client's cancellation, and a failure here is logged, never raised:
    /// the cached entries expire by themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync(Guid id)
    {
        var tenant = CacheTags.Tenant(user);
        try
        {
            string?[] tags = [CacheTags.Format(ItemTagTemplate, tenant, ("id", id)), CacheTags.Format(ListTagTemplate, tenant)];
            await cache.RemoveByTagAsync(tags.OfType<string>(), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for request {RequestId}; cached reads expire on their own", id);
        }
    }
}
