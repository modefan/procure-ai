using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// Who reads every request of the tenant, not only their scope (plan 07 Task 0c ruling): a signed-in user holding the tenant-admin
/// permission (<see cref="Permissions.AdminAuthorization"/>, the one the Access matrix endpoints need). A service acting for a user
/// never does: it is scoped like that user. The admin key and a service acting for nobody see everything through
/// <see cref="CallerScope.All"/> already. One decision for the list and the single-item reads.
/// </summary>
public static class WholeTenantReads
{
    public static async Task<bool> SeesWholeTenantAsync(
        this IPermissionProvider permissions, ICallerContext caller, ICurrentUser user, CancellationToken cancellationToken) =>
        caller.Kind is CallerKind.User
        && !user.IsServiceAccount
        && (await permissions.GetPermissionsAsync(user, cancellationToken)).Contains(Permissions.AdminAuthorization);
}
