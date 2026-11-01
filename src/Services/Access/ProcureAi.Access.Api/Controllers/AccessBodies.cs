using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Access.Api.Controllers;

/// <summary>The complete list of permissions the role should have afterwards.</summary>
public sealed record SetRolePermissionsBody(
    [Required, MaxLength(AccessLimits.MaxPermissions), EachMaxLength(AccessLimits.MaxCodeLength)] IReadOnlyList<string> Permissions);

/// <summary>The new approval limit in Rand.</summary>
public sealed record SetApprovalLimitBody([Range(0, AccessLimits.MaxApprovalLimit)] decimal Amount);

/// <summary>The complete list of cost center codes the user should have afterwards.</summary>
public sealed record SetUserCostCentersBody(
    [Required, MaxLength(AccessLimits.MaxCostCenters), EachMaxLength(AccessLimits.MaxCodeLength)] IReadOnlyList<string> Codes);

/// <summary>
/// A user who just signed up: the Keycloak user ID (<c>sub</c>), which must equal <c>X-On-Behalf-Of-User</c>. Email and name
/// are read from Keycloak. The ID is used in admin API paths, so it is limited to the characters a Keycloak ID has.
/// </summary>
public sealed record RegisterDemoUserBody([Required, RegularExpression(AccessLimits.UserIdPattern)] string UserId);

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class AccessLimits
{
    public const int MaxPermissions = 100;
    public const int MaxCodeLength = 100;
    public const int MaxCostCenters = 50;
    public const string UserIdPattern = "^[A-Za-z0-9-]{1,100}$";
    public const double MaxApprovalLimit = 1_000_000_000;
}

/// <summary>
/// Every element of a string list is at most <paramref name="length"/> characters (and not null), so unbounded input is refused as a
/// 400 before a handler can echo it into a 422 detail (final review F1). The message names the limit, never the value.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class EachMaxLengthAttribute(int length) : ValidationAttribute
{
    public int Length { get; } = length;

    public override bool IsValid(object? value) =>
        value is not IEnumerable<string?> items || items.All(item => item is not null && item.Length <= Length);

    public override string FormatErrorMessage(string name) => $"Every entry of {name} must be at most {Length} characters.";
}
