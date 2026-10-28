namespace ProcureAi.BuildingBlocks.Application.Access;

/// <summary>
/// What the Access service says about people and cost centers, for the services and the gateway. Answers are cached
/// (and evicted when Access publishes <c>AccessChanged</c>), so a call is cheap and survives a short Access outage.
/// </summary>
public interface IAccessDirectory
{
    /// <summary>What <paramref name="userId"/> may do in <paramref name="tenantId"/>, given the roles of their token.</summary>
    ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
        string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken);

    /// <summary>The manager, department head and finance approvers of a cost center.</summary>
    ValueTask<CostCenterApprovers> GetApproversAsync(string costCenterCode, string tenantId, CancellationToken cancellationToken);

    /// <summary>The name <paramref name="userId"/> is shown under in <paramref name="tenantId"/>, or null when the tenant has no such user.</summary>
    ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="userId"/> as <paramref name="tenantId"/> knows them (display name and email address, for notifications), or null
    /// when the tenant has no such user. The same cached lookup as <see cref="GetDisplayNameAsync"/>.
    /// </summary>
    ValueTask<DirectoryUser?> GetUserAsync(string userId, string tenantId, CancellationToken cancellationToken);
}

/// <summary>A user of a tenant: the name they are shown under and their email address (empty when Access has none).</summary>
public sealed record DirectoryUser(string UserId, string DisplayName, string Email);

/// <summary>Permissions, the cost centers the user acts for, and the approval limit in Rand.</summary>
public sealed record EffectiveAccess(IReadOnlyList<string> Permissions, IReadOnlyList<string> CostCenterCodes, decimal ApprovalLimit);

/// <summary>An approver candidate; <c>CanApprove</c> is true when their roles grant <c>requests.approve</c> in the tenant's matrix.</summary>
public sealed record Approver(string UserId, string DisplayName, decimal ApprovalLimit, bool CanApprove);

public sealed record CostCenterApprovers(
    string CostCenterCode, string DepartmentCode, Approver? Manager, Approver? DepartmentHead, IReadOnlyList<Approver> Finance);
