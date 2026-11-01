using Microsoft.AspNetCore.Authorization;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// Policy for the operations endpoints that act on every tenant at once (the demo reset): only the admin API key, never a service account
/// and never a user token, not even an admin's. Stricter than <see cref="InternalPolicy"/>: a service account that is compromised or
/// misconfigured can call the other services' internal endpoints, but cannot wipe their data.
/// </summary>
public static class AdminApiKeyPolicy
{
    public const string Name = "admin-api-key";
}

internal sealed class AdminApiKeyRequirement : IAuthorizationRequirement;

internal sealed class AdminApiKeyAuthorizationHandler(ICurrentUser user) : AuthorizationHandler<AdminApiKeyRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminApiKeyRequirement requirement)
    {
        if (user.IsAdminApiKey)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
