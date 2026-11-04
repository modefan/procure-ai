using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application;
using ProcureAi.Approvals.Application.AuthorizationCheck;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Queries;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Paging;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using Wolverine;
using CheckAuthorizationQuery = ProcureAi.Approvals.Application.AuthorizationCheck.CheckAuthorization;

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
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ICallerContext caller, IPermissionProvider permissions,
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
        [FromQuery] PagingQuery paging, CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<ApprovalStepDto>>(new GetInbox(paging.Page, paging.Size()), cancellationToken);
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
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<ApprovalStepDto>> Approve(
        Guid stepId, [FromBody] ApproveStepBody? body = null, CancellationToken cancellationToken = default)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var step = await bus.InvokeAsync<ApprovalStepDto>(new GetApprovalStep(stepId), cancellationToken);
        await bus.InvokeOrConflictAsync(new ApproveStep(step.RequestId, stepId, version, body?.Comment), Approval, cancellationToken);
        return await ActedAsync(step, ApprovalStepStatus.Approved);
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
        return await ActedAsync(step, ApprovalStepStatus.Rejected);
    }

    /// <summary>
    /// The approval chain of a request: its state and every step, with what the caller may do on each. Scoped like the request
    /// (requester, its cost center's people, the tenant admin, the admin key) plus the approvers of its steps; anyone else gets 404.
    /// </summary>
    [HttpGet("requests/{requestId:guid}", Name = "GetApprovalChain")]
    [CachedGet(Tags = [ApprovalCacheTag.ChainTemplate])]
    [RequirePermission(Permissions.RequestsRead)]
    [ProducesResponseType<ApprovalChainDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApprovalChainDto>> Chain(Guid requestId, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<ApprovalChainDto>(
            new GetApprovalChain(requestId, await permissions.SeesWholeTenantAsync(caller, user, cancellationToken)), cancellationToken);

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
    /// Admin: why <c>userId</c> may or may not <c>action</c> (<c>view</c> or <c>approve</c>) request <c>requestId</c> of the tenant, step
    /// by step, by the same rules the APIs and the approval routing use. 404 when the tenant has no such user or request.
    /// </summary>
    [HttpGet("authorization-check", Name = "CheckAuthorization")]
    [CachedGet(Tags = [AccessCacheTag.Template])]
    [RequirePermission(Permissions.AdminAuthorization)]
    [ProducesResponseType<AuthorizationCheckDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthorizationCheckDto>> CheckAuthorization(
        [FromQuery, Required, MaxLength(ApprovalLimits.MaxUserIdLength)] string userId,
        [FromQuery, Required] Guid requestId,
        [FromQuery, Required, RegularExpression("^(view|approve)$")] string action,
        CancellationToken cancellationToken) =>
        await bus.InvokeAsync<AuthorizationCheckDto>(new CheckAuthorizationQuery(userId, requestId, action), cancellationToken);

    /// <summary>
    /// After the saga committed: evicts the cached inbox and chain at once (the saga's own eviction follows through its outbox),
    /// then answers with the step as it is now and its new ETag. The write is done, so the answer must not depend on a read that
    /// can fail: if the read fails, the answer is built from the step as read before the action, now closed as
    /// <paramref name="outcome"/>, without an ETag (the new version is not known; nothing more can be done on a closed step, and the
    /// next read of the step or the chain has it). Otherwise the client would retry and get 409 "not open" for what succeeded.
    /// </summary>
    private async Task<ActionResult<ApprovalStepDto>> ActedAsync(ApprovalStepDto before, ApprovalStepStatus outcome)
    {
        try
        {
            await cache.RemoveByTagAsync(ApprovalCacheTag.Of(user.TenantId, before.RequestId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for the approval of {RequestId}; cached reads expire on their own", before.RequestId);
        }

        // The write has committed: a client that disconnects now must not turn it into a failure (its retry would run it again).
        try
        {
            var step = await bus.InvokeAsync<ApprovalStepDto>(new GetApprovalStep(before.Id), CancellationToken.None);
            Response.Headers.ETag = VersionETag.Format(step.Version);
            return step;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Step {StepId} of {RequestId} was {Outcome} but could not be read back; answering from what was read before",
                before.Id, before.RequestId, outcome);
            return before with
            {
                Status = outcome.ToString(),
                DueAt = null,
                CanAct = false,
                CannotActReason = ApprovalStepView.NotOpen,
            };
        }
    }
}
