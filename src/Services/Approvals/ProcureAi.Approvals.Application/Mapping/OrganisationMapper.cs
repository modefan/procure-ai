using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using Riok.Mapperly.Abstractions;

namespace ProcureAi.Approvals.Application.Mapping;

/// <summary>The Access service's answer about a cost center, as the routing engine's snapshot (limits in Rand).</summary>
[Mapper]
public static partial class OrganisationMapper
{
    [MapperIgnoreSource(nameof(CostCenterApprovers.DepartmentCode))]
    public static partial OrganisationSnapshot ToSnapshot(CostCenterApprovers approvers);

    [MapperIgnoreSource(nameof(Approver.DisplayName))]
    private static partial Candidate ToCandidate(Approver approver);

    private static Money ToRand(decimal amount) => Money.Zar(amount);
}
