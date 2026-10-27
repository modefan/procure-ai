using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Mapping;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>
/// Who would approve a request of <c>Amount</c> (Rand incl. VAT) on <c>CostCenterCode</c> raised by <c>RequesterId</c> in the
/// caller's tenant, by the same routing the saga uses when the budget is reserved.
/// </summary>
public sealed record PreviewApproval(string CostCenterCode, decimal Amount, string RequesterId);

public static class PreviewApprovalHandler
{
    public static async Task<ApprovalPreviewDto> Handle(
        PreviewApproval query, IAccessDirectory access, ICallerContext caller, IOptions<ApprovalsOptions> options,
        CancellationToken cancellationToken)
    {
        if (query.Amount <= 0)
        {
            throw new DomainException("The amount must be more than zero.");
        }

        var tenant = caller.TenantId ?? throw new ForbiddenException("A tenant is required.");
        CostCenterApprovers approvers;
        try
        {
            approvers = await access.GetApproversAsync(query.CostCenterCode, tenant, cancellationToken);
        }
        catch (NotFoundException)
        {
            return NotPossible(ApprovalSaga.UnknownCostCenter(query.CostCenterCode));
        }

        ApprovalChain chain;
        try
        {
            chain = ApprovalRouting.Resolve(
                Money.Zar(query.Amount), query.RequesterId, OrganisationMapper.ToSnapshot(approvers), options.Value.ToThresholds());
        }
        catch (NoEligibleApproverException exception)
        {
            return NotPossible(exception.Message);
        }

        var names = new[] { approvers.Manager, approvers.DepartmentHead }.OfType<Approver>().Concat(approvers.Finance)
            .GroupBy(a => a.UserId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);
        return new ApprovalPreviewDto(
            true,
            null,
            [.. chain.Steps.Select(s => new PreviewStepDto(
                s.StepNo, s.Level.ToString(), [.. s.ApproverIds.Select(id => names.GetValueOrDefault(id, id))]))]);
    }

    private static ApprovalPreviewDto NotPossible(string reason) => new(false, reason, []);
}
