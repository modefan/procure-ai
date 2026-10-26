using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Access.Api.Controllers;

/// <summary>The complete list of permissions the role should have afterwards.</summary>
public sealed record SetRolePermissionsBody([Required, MaxLength(AccessLimits.MaxPermissions)] IReadOnlyList<string> Permissions);

/// <summary>The new approval limit in Rand.</summary>
public sealed record SetApprovalLimitBody([Range(0, AccessLimits.MaxApprovalLimit)] decimal Amount);

/// <summary>
/// A user who just signed up, as the gateway read it from their own token: the Keycloak user ID (<c>sub</c>), email and name.
/// The ID is used in admin API paths, so it is limited to the characters a Keycloak ID has.
/// </summary>
public sealed record RegisterDemoUserBody(
    [Required, RegularExpression(AccessLimits.UserIdPattern)] string UserId,
    [Required, MaxLength(AccessLimits.MaxEmailLength)] string Email,
    [MaxLength(AccessLimits.MaxNameLength)] string DisplayName);

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class AccessLimits
{
    public const int MaxPermissions = 100;
    public const int MaxCodeLength = 100;
    public const int MaxEmailLength = 320;
    public const int MaxNameLength = 200;
    public const string UserIdPattern = "^[A-Za-z0-9-]{1,100}$";
    public const double MaxApprovalLimit = 1_000_000_000;
}
