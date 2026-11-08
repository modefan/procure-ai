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

/// <summary>The answer to a registration: the tenant the user is in, and whether this call added them (false: they were already in).</summary>
public sealed record RegistrationDto(string TenantId, bool Joined);

/// <summary>The name a user is shown under (audit history, inboxes), their email address (bell notifications by email) and the name of their tenant.</summary>
public sealed record UserNameDto(string UserId, string DisplayName, string Email, string? TenantName = null);

/// <summary>A cost center the caller acts for.</summary>
public sealed record CostCenterDto(string Code, string Name, string DepartmentCode);

/// <summary>
/// The caller as the screens need them: display name (the user ID when the tenant has no such user), the roles of the token, the
/// cost centers they act for (finance: all of the tenant) and the approval limit in Rand (null when they have none).
/// </summary>
public sealed record MeDto(
    string UserId, string Name, IReadOnlyList<string> Roles, IReadOnlyList<CostCenterDto> CostCenters, decimal? ApprovalLimit,
    string TenantName = "");

/// <summary>A tenant a user belongs to: its ID (the token's tenant claim) and the name it is shown under.</summary>
public sealed record TenantDto(string Id, string Name);

/// <summary>A cost center with its department, manager and department head (names are null when nobody is assigned or the user is gone).</summary>
public sealed record CostCenterAdminDto(
    string Code, string Name, string DepartmentCode, string DepartmentName, string? ManagerId, string? ManagerName,
    string? DepartmentHeadId, string? DepartmentHeadName);

/// <summary>
/// Why a user may do what they may: the roles stored for them, for each permission the roles that grant it, the cost centers
/// they act for, the departments they head, the approval limit in Rand and the finance flag.
/// </summary>
public sealed record AccessExplanationDto(
    string UserId, string DisplayName, string TenantId, IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionSources, IReadOnlyList<string> CostCenterCodes,
    IReadOnlyList<string> DepartmentCodes, decimal ApprovalLimit, bool IsFinanceApprover);

/// <summary>Whether the demo is switched on, and what the maintenance page says while it is off (null: its default text).</summary>
public sealed record SystemAvailabilityDto(bool Enabled, string? Message);
