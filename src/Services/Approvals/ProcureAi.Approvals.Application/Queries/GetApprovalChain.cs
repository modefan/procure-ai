using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>The approval chain of a request of the caller's tenant.</summary>
public sealed record GetApprovalChain(Guid RequestId);

/// <summary>One step of the caller's tenant (for the answer to an approve or reject, and to find the step's request).</summary>
public sealed record GetApprovalStep(Guid StepId);

public static class ApprovalQueriesHandler
{
    public static async Task<ApprovalChainDto> Handle(
        GetApprovalChain query, IApprovalsDbContext db, IAccessDirectory access, ICallerContext caller,
        CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId;
        var saga = await db.Sagas.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == query.RequestId && s.TenantId == tenant, cancellationToken);
        // Steps of a completed saga stay; an escalated step comes before its replacement (version 7 IDs grow with time).
        var steps = await db.Steps.AsNoTracking()
            .Where(s => s.RequestId == query.RequestId)
            .OrderBy(s => s.StepNo)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        if (saga is null && steps.Count == 0)
        {
            throw new NotFoundException($"No approval for request {query.RequestId}.");
        }

        var requesterId = saga?.RequesterId ?? steps.FirstOrDefault()?.RequesterId;
        var requesterName = requesterId is null ? null : await NameOfAsync(access, requesterId, tenant ?? "", cancellationToken);
        return new ApprovalChainDto(
            query.RequestId,
            StateOf(saga, steps).ToString(),
            [.. steps.Select(s => ApprovalStepView.ToDto(s, caller.UserId))],
            requesterName);
    }

    public static async Task<ApprovalStepDto> Handle(
        GetApprovalStep query, IApprovalsDbContext db, ICallerContext caller, CancellationToken cancellationToken)
    {
        var step = await db.Steps.AsNoTracking().SingleOrDefaultAsync(s => s.Id == query.StepId, cancellationToken)
            ?? throw new NotFoundException($"Approval step {query.StepId} was not found.");
        return ApprovalStepView.ToDto(step, caller.UserId);
    }

    /// <summary>The requester's display name; null when Access cannot give it (the chain must not fail because of a name).</summary>
    private static async Task<string?> NameOfAsync(IAccessDirectory access, string userId, string tenant, CancellationToken cancellationToken)
    {
        try
        {
            return await access.GetDisplayNameAsync(userId, tenant, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The approval's outcome. A rejected step is final: the chain stays Rejected even when a cancellation arrived after the
    /// rejection and left a cancelled marker (the request's own outcome, Cancelled, is Requests' to show). Otherwise the saga's
    /// state; a saga that completed was deleted, and without a rejected step it was cancelled. (A rejection without steps,
    /// refused by Budgets or with no eligible approver, reads Cancelled after such a late cancellation.)
    /// </summary>
    private static ApprovalState StateOf(ApprovalSaga? saga, List<ApprovalStep> steps) =>
        steps.Any(s => s.Status == ApprovalStepStatus.Rejected) ? ApprovalState.Rejected : saga?.State ?? ApprovalState.Cancelled;
}
