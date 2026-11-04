using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>
/// The approval chain of a request of the caller's tenant, for the callers who may read the request (its requester, its cost
/// center's people, the tenant admin: <paramref name="WholeTenant"/>, decided by the Api as for the Requests list) and for the
/// approvers of its steps.
/// </summary>
public sealed record GetApprovalChain(Guid RequestId, bool WholeTenant = false);

/// <summary>One step of the caller's tenant (for the answer to an approve or reject, and to find the step's request).</summary>
public sealed record GetApprovalStep(Guid StepId);

public static class ApprovalQueriesHandler
{
    public static async Task<ApprovalChainDto> Handle(
        GetApprovalChain query, IApprovalsDbContext db, IAccessDirectory access, ICallerContext caller, ICallerScope scope,
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

        // A cancelled marker (a cancellation that came after the saga ended) knows neither requester nor cost center: the steps do.
        var requesterId = NonEmpty(saga?.RequesterId) ?? steps.FirstOrDefault()?.RequesterId;
        var costCenterCode = NonEmpty(saga?.CostCenterCode) ?? steps.FirstOrDefault()?.CostCenterCode;
        if (!query.WholeTenant && !await MayReadAsync(scope, caller, requesterId, costCenterCode, steps, cancellationToken))
        {
            // Out of scope reads like a missing chain, so the answer does not tell that the request exists.
            throw new NotFoundException($"No approval for request {query.RequestId}.");
        }

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

    /// <summary>
    /// The request's scope (<see cref="CallerScope.Allows"/>: everything, the requester, or one of the caller's cost centers, the same
    /// rule as the Requests reads), or the caller is or was an approver of one of its steps (they must see the chain they act on). A
    /// step planned before its cost center was kept has none: then only the requester, the approvers and the wide callers see it.
    /// </summary>
    private static async Task<bool> MayReadAsync(
        ICallerScope scope, ICallerContext caller, string? requesterId, string? costCenterCode, List<ApprovalStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Any(s => s.ApproverIds.Contains(caller.UserId, StringComparer.Ordinal) || s.ActedBy == caller.UserId))
        {
            return true;
        }

        return (await scope.GetAsync(cancellationToken)).Allows(requesterId ?? "", costCenterCode ?? "");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

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
