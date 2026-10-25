using Microsoft.AspNetCore.Authorization;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// Policy for the <c>.../internal/...</c> endpoints other services call: allowed for a service account (a token of the
/// internal client) and for the admin API key, never for a normal user. Use
/// <c>[Authorize(Policy = InternalPolicy.Name)]</c>.
/// </summary>
public static class InternalPolicy
{
    public const string Name = "internal";
}

internal sealed class InternalRequirement : IAuthorizationRequirement;

internal sealed class InternalAuthorizationHandler(ICurrentUser user) : AuthorizationHandler<InternalRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, InternalRequirement requirement)
    {
        if (user.IsAdminApiKey || user.IsServiceAccount)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
