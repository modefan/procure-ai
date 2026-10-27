using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Queries;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using Wolverine;

namespace ProcureAi.Approvals.Api.Controllers;

/// <summary>
/// Approvals of the caller's tenant: the approver's inbox, approve and reject (checked against the step version in
/// <c>If-Match</c>), the chain of a request and the preview of who would approve. Approve and reject are handled by the
/// request's approval saga, in process.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approvals")]
[RequireTenant]
public sealed class ApprovalsController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ICallerContext caller, IOptions<PagingOptions> paging,
    ILogger<ApprovalsController> logger) : ControllerBase
{
    /// <summary>
    /// Approve and reject run in the request's saga, in process. A saga message that lost a race is retried inline
    /// (<c>Approvals:ConcurrencyRetry</c>, which applies to <c>InvokeAsync</c> too) and then sees the winner's outcome (409 / 412);
    /// if even the retries lose, <see cref="ConcurrencyConflicts.InvokeOrConflictAsync"/> answers 409, never 500.
    /// </summary>
    private const string Approval = "The approval";

    /// <summary>The open steps the caller can approve or reject, the most urgent first.</summary>
    [HttpGet("inbox", Name = "GetApprovalInbox")]
    [CachedGet(Tags = [ApprovalCacheTag.InboxTemplate])]
    [RequirePermission(Permissions.RequestsApprove)]
    [ProducesResponseType<PagedResult<ApprovalStepDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ApprovalStepDto>>> Inbox(
        [FromQuery] int page = 1, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
    {
        // Bad query input is a 400 validation problem; the limit comes from configuration (Paging), so it cannot be an attribute.
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

        return await bus.InvokeAsync<PagedResult<ApprovalStepDto>>(new GetInbox(page, size), cancellationToken);
    }

    /// <summary>
    /// Approves the step. Send the step version (<c>version</c> of the inbox or chain) in <c>If-Match</c>. 409 when the step is
    /// not open any more (approved, rejected, escalated), 412 when it changed since it was read, 403 for the requester and for
    /// anyone who is not one of its approvers.
    /// </summary>
    [HttpPost("{stepId:guid}/approve", Name = "ApproveStep")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsApprove)]
    [RequireIfMatch]
    [ProducesResponseType<ApprovalStepDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<ApprovalStepDto>> Approve(Guid stepId, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var step = await bus.InvokeAsync<ApprovalStepDto>(new GetApprovalStep(stepId), cancellationToken);
        await bus.InvokeOrConflictAsync(new ApproveStep(step.RequestId, stepId, version), Approval, cancellationToken);
        return await ActedAsync(step.RequestId, stepId);
    }

    /// <summary>Rejects the step and with it the request (the budget is released). Same rules as approve; a reason is required.</summary>
    [HttpPost("{stepId:guid}/reject", Name = "RejectStep")]
    [Idempotent]
    [RequirePermission(Permissions.RequestsApprove)]
    [RequireIfMatch]
    [ProducesResponseType<ApprovalStepDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<ApprovalStepDto>> Reject(Guid stepId, RejectStepBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var step = await bus.InvokeAsync<ApprovalStepDto>(new GetApprovalStep(stepId), cancellationToken);
        await bus.InvokeOrConflictAsync(new RejectStep(step.RequestId, stepId, version, body.Reason.Trim()), Approval, cancellationToken);
        return await ActedAsync(step.RequestId, stepId);
    }

    /// <summary>The approval chain of a request: its state and every step, with what the caller may do on each.</summary>
    [HttpGet("requests/{requestId:guid}", Name = "GetApprovalChain")]
    [CachedGet(Tags = [ApprovalCacheTag.ChainTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<ApprovalChainDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApprovalChainDto>> Chain(Guid requestId, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<ApprovalChainDto>(new GetApprovalChain(requestId), cancellationToken);

    /// <summary>Who would approve a request of <c>amount</c> (Rand incl. VAT) on <c>costCenter</c> raised by the caller, or why nobody can.</summary>
    [HttpGet("preview", Name = "PreviewApproval")]
    [CachedGet(Tags = [AccessCacheTag.Template])]
    [RequirePermission(Permissions.RequestsCreate)]
    [ProducesResponseType<ApprovalPreviewDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApprovalPreviewDto>> Preview(
        [FromQuery, Required, MaxLength(ApprovalLimits.MaxCodeLength)] string costCenter,
        [FromQuery, Range(ApprovalLimits.MinAmount, ApprovalLimits.MaxAmount)] decimal amount,
        CancellationToken cancellationToken) =>
        await bus.InvokeAsync<ApprovalPreviewDto>(new PreviewApproval(costCenter, amount, caller.UserId), cancellationToken);

    /// <summary>
    /// Internal: the preview for another service (Requests checks it before a submission), for <c>requesterId</c> in the tenant of
    /// <c>X-Tenant-Id</c>. Service account or admin API key.
    /// </summary>
    [HttpGet("internal/preview", Name = "PreviewApprovalInternal")]
    [CachedGet(Tags = [AccessCacheTag.Template])]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<ApprovalPreviewDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApprovalPreviewDto>> PreviewInternal(
        [FromQuery, Required, MaxLength(ApprovalLimits.MaxCodeLength)] string costCenter,
        [FromQuery, Range(ApprovalLimits.MinAmount, ApprovalLimits.MaxAmount)] decimal amount,
        [FromQuery, Required, MaxLength(ApprovalLimits.MaxUserIdLength)] string requesterId,
        CancellationToken cancellationToken) =>
        await bus.InvokeAsync<ApprovalPreviewDto>(new PreviewApproval(costCenter, amount, requesterId), cancellationToken);

    /// <summary>
    /// After the saga committed: evicts the cached inbox and chain at once (the saga's own eviction follows through its outbox),
    /// then answers with the step as it is now and its new ETag.
    /// </summary>
    private async Task<ActionResult<ApprovalStepDto>> ActedAsync(Guid requestId, Guid stepId)
    {
        try
        {
            await cache.RemoveByTagAsync(ApprovalCacheTag.Of(user.TenantId, requestId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for the approval of {RequestId}; cached reads expire on their own", requestId);
        }

        // The write has committed: a client that disconnects now must not turn it into a failure (its retry would run it again).
        var step = await bus.InvokeAsync<ApprovalStepDto>(new GetApprovalStep(stepId), CancellationToken.None);
        Response.Headers.ETag = VersionETag.Format(step.Version);
        return step;
    }
}
