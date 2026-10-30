using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using Riok.Mapperly.Abstractions;

namespace ProcureAi.Approvals.Application.Mapping;

/// <summary>The Access service's answer about a cost center, as the routing engine's snapshot (limits in Rand; names and the department only for the texts of the authorization check).</summary>
[Mapper]
public static partial class OrganisationMapper
{
    public static partial OrganisationSnapshot ToSnapshot(CostCenterApprovers approvers);

    private static partial Candidate ToCandidate(Approver approver);

    private static Money ToRand(decimal amount) => Money.Zar(amount);
}
