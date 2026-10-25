using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, IOptions<PagingOptions> paging) : ControllerBase
{
    // The same two tags are dropped by InvalidateAsync after a write.
    private const string ItemTagTemplate = "requests:{tenant}:{id}";
    private const string ListTagTemplate = "requests:{tenant}:list";

    [HttpPost]
    [Idempotent]
    [RequirePermission(Permissions.RequestsCreate)]
    [ProducesResponseType<RequestDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RequestDto>> Create(CreateRequestBody body, CancellationToken cancellationToken)
    {
        var created = await bus.InvokeAsync<RequestDto>(body.ToCommand(), cancellationToken);
        await InvalidateAsync(created.Id, cancellationToken);
        Response.Headers.ETag = VersionETag.Format(created.Version);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    [CachedGet(Tags = [ItemTagTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var request = await bus.InvokeAsync<RequestDto>(new GetRequest(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(request.Version);
        return request;
    }

    [HttpGet]
    [CachedGet(Tags = [ListTagTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<PagedResult<RequestDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<RequestDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default) =>
        await bus.InvokeAsync<PagedResult<RequestDto>>(
            new ListRequests(page, pageSize ?? paging.Value.DefaultPageSize, status), cancellationToken);

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status200OK)]
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
        await InvalidateAsync(id, cancellationToken);
        Response.Headers.ETag = VersionETag.Format(updated.Version);
        return updated;
    }

    /// <summary>Submits a draft for approval. Returns the request as it is now, with its new ETag.</summary>
    [HttpPost("{id:guid}/submit")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsCreate)]
    [RequireIfMatch]
    [ProducesResponseType<RequestDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<RequestDto>> Submit(Guid id, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        await bus.InvokeAsync(new SubmitRequest(id, version), cancellationToken);
        await InvalidateAsync(id, cancellationToken);
        var submitted = await bus.InvokeAsync<RequestDto>(new GetRequest(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(submitted.Version);
        return AcceptedAtAction(nameof(Get), new { id }, submitted);
    }

    /// <summary>Drops the cached reads of this request and of the tenant list, so no read after a write sees the old state.</summary>
    private Task InvalidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = CacheTags.Tenant(user);
        return cache.RemoveByTagAsync([$"requests:{tenant}:{id}", $"requests:{tenant}:list"], cancellationToken);
    }
}
