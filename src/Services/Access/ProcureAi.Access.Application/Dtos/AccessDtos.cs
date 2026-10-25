namespace ProcureAi.Access.Application.Dtos;

/// <summary>What one user may do: permissions, the cost centers they act for, and the approval limit in Rand.</summary>
public sealed record EffectiveAccessDto(
    IReadOnlyList<string> Permissions, IReadOnlyList<string> CostCenterCodes, decimal ApprovalLimit);

/// <summary>An approver candidate. CanApprove is true when the stored roles of the user grant requests.approve in this tenant matrix.</summary>
public sealed record ApproverDto(string UserId, string DisplayName, decimal ApprovalLimit, bool CanApprove);

public sealed record CostCenterApproversDto(
    string CostCenterCode, string DepartmentCode, ApproverDto? Manager, ApproverDto? DepartmentHead, IReadOnlyList<ApproverDto> Finance);

public sealed record RoleDto(string Role, IReadOnlyList<string> Permissions);

/// <summary>The tenant role to permission matrix. Version is the ETag the next change must send in If-Match.</summary>
public sealed record MatrixDto(
    string TenantId, uint Version, IReadOnlyList<string> AvailablePermissions, IReadOnlyList<RoleDto> Roles);

public sealed record UserDto(
    string UserId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    decimal ApprovalLimit,
    bool IsFinanceApprover,
    IReadOnlyList<string> CostCenterCodes,
    uint Version);
