using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>The approval chain of a request of the caller's tenant.</summary>
public sealed record GetApprovalChain(Guid RequestId);

/// <summary>One step of the caller's tenant (for the answer to an approve or reject, and to find the step's request).</summary>
public sealed record GetApprovalStep(Guid StepId);

public static class ApprovalQueriesHandler
{
    public static async Task<ApprovalChainDto> Handle(
        GetApprovalChain query, IApprovalsDbContext db, ICallerContext caller, CancellationToken cancellationToken)
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

        return new ApprovalChainDto(
            query.RequestId,
            (saga?.State ?? EndedState(steps)).ToString(),
            [.. steps.Select(s => ApprovalStepView.ToDto(s, caller.UserId))]);
    }

    public static async Task<ApprovalStepDto> Handle(
        GetApprovalStep query, IApprovalsDbContext db, ICallerContext caller, CancellationToken cancellationToken)
    {
        var step = await db.Steps.AsNoTracking().SingleOrDefaultAsync(s => s.Id == query.StepId, cancellationToken)
            ?? throw new NotFoundException($"Approval step {query.StepId} was not found.");
        return ApprovalStepView.ToDto(step, caller.UserId);
    }

    /// <summary>A saga that completed was deleted: a rejected step says it was rejected, otherwise it was cancelled.</summary>
    private static ApprovalState EndedState(List<ApprovalStep> steps) =>
        steps.Any(s => s.Status == ApprovalStepStatus.Rejected) ? ApprovalState.Rejected : ApprovalState.Cancelled;
}
