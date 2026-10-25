using Microsoft.AspNetCore.Authorization;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Requires a permission; maps to the policy <c>perm:&lt;name&gt;</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission)
    : AuthorizeAttribute(PermissionPolicyProvider.PolicyPrefix + permission);
