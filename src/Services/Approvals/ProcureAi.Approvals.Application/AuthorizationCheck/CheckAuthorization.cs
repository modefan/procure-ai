using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Mapping;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Application.AuthorizationCheck;

/// <summary>
/// Why <c>UserId</c> may or may not <c>Action</c> (<see cref="CheckAuthorizationHandler.View"/> or
/// <see cref="CheckAuthorizationHandler.Approve"/>) request <c>RequestId</c> of the caller's tenant.
/// </summary>
public sealed record CheckAuthorization(string UserId, Guid RequestId, string Action);

/// <summary>
/// The admin's "why can / can't they?" (plan 06 ruling 6), by the rules the APIs use: the permission from the user's stored roles
/// (Access's explanation names the granting roles), the tenant, and for <c>approve</c> the open step, the
/// <see cref="ApproverEligibility"/> reasons at that step's level (what routing decides with) and
/// <see cref="ApprovalStep.MayBeActedOnBy"/> (what the approve action checks). A user or a request the caller's tenant does not
/// have is 404, so another tenant's IDs reveal nothing. Only the steps the API enforces gate the outcome; the routing-eligibility
/// reasons are explanation (see <see cref="AuthorizationCheckDto"/>).
/// </summary>
public static class CheckAuthorizationHandler
{
    public const string View = "view";
    public const string Approve = "approve";

    public static async Task<AuthorizationCheckDto> Handle(
        CheckAuthorization query, IApprovalsDbContext db, IAccessDirectory access, ICallerContext caller,
        CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new ForbiddenException("A tenant is required.");
        if (query.Action is not (View or Approve))
        {
            throw new DomainException($"The action must be {View} or {Approve}.");
        }

        var saga = await db.Sagas.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == query.RequestId && s.TenantId == tenant, cancellationToken);
        // Steps are filtered to the caller's tenant; an escalated step comes before its replacement.
        var steps = await db.Steps.AsNoTracking()
            .Where(s => s.RequestId == query.RequestId)
            .OrderBy(s => s.StepNo)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        if (saga is null && steps.Count == 0)
        {
            throw new NotFoundException($"No request {query.RequestId}.");
        }

        var explanation = await access.ExplainAsync(query.UserId, tenant, cancellationToken)
            ?? throw new NotFoundException($"User {query.UserId} was not found.");
        var tenantName = (await access.GetUserAsync(query.UserId, tenant, cancellationToken))?.TenantName;
        var name = string.IsNullOrWhiteSpace(explanation.DisplayName) ? explanation.UserId : explanation.DisplayName;
        // A saga stored before its chain existed may have neither a number nor steps (final review F3): fall back to the ID.
        var number = saga is { RequestNumber.Length: > 0 } ? saga.RequestNumber
            : steps.FirstOrDefault(s => s.RequestNumber.Length > 0)?.RequestNumber ?? query.RequestId.ToString();

        var permission = query.Action == View ? Permissions.RequestsRead : Permissions.RequestsApprove;
        var result = new List<CheckStepDto>
        {
            PermissionStep(permission, name, explanation),
            Gate("tenant", true, $"Same tenant ({(string.IsNullOrWhiteSpace(tenantName) ? tenant : tenantName)})"),
        };

        if (query.Action == Approve)
        {
            result.AddRange(await ApproveStepsAsync(saga?.CostCenterCode, steps, explanation, name, tenant, access, cancellationToken));
        }

        return new AuthorizationCheckDto(result.Where(s => s.Gating).All(s => s.Passed), query.Action, name, number, result);
    }

    private static async Task<IEnumerable<CheckStepDto>> ApproveStepsAsync(
        string? costCenterCode, List<ApprovalStep> steps, AccessExplanation explanation, string name, string tenant,
        IAccessDirectory access, CancellationToken cancellationToken)
    {
        var open = steps.FirstOrDefault(s => s.IsOpen);
        if (open is null)
        {
            return [Gate("open-step", false, "The request has no open approval step")];
        }

        var result = new List<CheckStepDto>
        {
            Gate("open-step", true, $"Step {open.StepNo} ({LevelName(open.Level)}) is open"),
        };

        var org = await OrganisationAsync(costCenterCode, tenant, access, cancellationToken);
        var candidate = new Candidate(
            explanation.UserId, Money.Zar(explanation.ApprovalLimit),
            explanation.PermissionSources.ContainsKey(Permissions.RequestsApprove), name);
        var facts = new RequestFacts(open.TotalInclVat, open.RequesterId, org);
        // The permission reason is already the first step (with the roles that grant it).
        // Separation of duties is enforced by the approve action (MayBeActedOnBy), so it gates. The other reasons are what routing
        // would decide with Access's data today; the action does not re-check them, so they explain but never decide.
        result.AddRange(ApproverEligibility.Check(LevelToExplain(open, candidate, facts), candidate, facts)
            .Where(r => r.Code != ApproverEligibility.Permission)
            .Select(r => r.Code == ApproverEligibility.NotRequester
                ? Gate(r.Code, r.Passed, r.Text)
                : new CheckStepDto(r.Code, r.Passed, r.Passed ? r.Text : $"Routing today would not pick {name}: {r.Text}", Gating: false)));

        var mayAct = open.MayBeActedOnBy(explanation.UserId);
        result.Add(Gate("current-approver", mayAct, mayAct
            ? $"{name} is an approver of step {open.StepNo}"
            : $"{name} is not an approver of step {open.StepNo}"));
        return result;
    }

    /// <summary>
    /// The level whose reasons explain the user best: of the levels the open step stands for (lowest first), the one where the
    /// cost center's organisation has the user (a manager whose level moved up because she raised the request sees the manager's
    /// reasons, and which one failed); otherwise the level the step landed on.
    /// </summary>
    private static ApprovalLevel LevelToExplain(ApprovalStep open, Candidate candidate, RequestFacts facts) =>
        open.CoversLevels.Append(open.Level).Distinct().Order()
            .Where(level => ApproverEligibility.Check(level, candidate, facts).Any(r => r.Passed
                && r.Code is ApproverEligibility.CostCenter or ApproverEligibility.Department or ApproverEligibility.Finance))
            .DefaultIfEmpty(open.Level)
            .First();

    /// <summary>The organisation of the request's cost center as Access gives it now (what routing uses); empty when Access no longer knows it.</summary>
    private static async Task<OrganisationSnapshot> OrganisationAsync(
        string? costCenterCode, string tenant, IAccessDirectory access, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(costCenterCode))
        {
            return new OrganisationSnapshot("", null, null, []);
        }

        try
        {
            return OrganisationMapper.ToSnapshot(await access.GetApproversAsync(costCenterCode, tenant, cancellationToken));
        }
        catch (NotFoundException)
        {
            return new OrganisationSnapshot(costCenterCode, null, null, []);
        }
    }

    private static CheckStepDto PermissionStep(string permission, string name, AccessExplanation explanation)
    {
        if (explanation.PermissionSources.TryGetValue(permission, out var granting) && granting.Count > 0)
        {
            var roles = string.Join(", ", granting.Select(RoleName));
            return Gate("permission", true, granting.Count == 1 ? $"Role {roles} has {permission}" : $"Roles {roles} have {permission}");
        }

        return explanation.Roles.Count switch
        {
            0 => Gate("permission", false, $"{name} has no role with {permission}"),
            1 => Gate("permission", false, $"Role {RoleName(explanation.Roles[0])} does not have {permission}"),
            _ => Gate("permission", false, $"Roles {string.Join(", ", explanation.Roles.Select(RoleName))} do not have {permission}"),
        };
    }

    /// <summary>A step the API enforces: it decides <see cref="AuthorizationCheckDto.Allowed"/>.</summary>
    private static CheckStepDto Gate(string code, bool passed, string text) => new(code, passed, text, Gating: true);

    /// <summary><c>department-head</c> → "Department head", <c>manager</c> → "Manager".</summary>
    private static string RoleName(string role)
    {
        var words = role.Replace('-', ' ').Trim();
        return words.Length == 0 ? role : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string LevelName(ApprovalLevel level) => level == ApprovalLevel.DepartmentHead ? "Department head" : level.ToString();
}
