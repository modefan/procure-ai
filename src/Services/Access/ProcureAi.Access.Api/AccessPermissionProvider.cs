using ProcureAi.Access.Application.Queries;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using Wolverine;

namespace ProcureAi.Access.Api;

/// <summary>
/// The permissions of a caller: the admin API key has all of them; everyone else gets what the roles of their token
/// hold in the matrix of their tenant (the token is authoritative for the roles of the caller). Read from this
/// service own database through the same query handlers as everything else, so a matrix change applies to the next request.
/// </summary>
public sealed class AccessPermissionProvider(IMessageBus bus) : IPermissionProvider
{
    public async ValueTask<IReadOnlySet<string>> GetPermissionsAsync(ICurrentUser user, CancellationToken ct)
    {
        if (user.IsAdminApiKey)
        {
            return Permissions.All;
        }

        if (user.TenantId is null || user.Roles.Count == 0)
        {
            return new HashSet<string>();
        }

        var permissions = await bus.InvokeAsync<IReadOnlyList<string>>(new GetRolePermissions([.. user.Roles]), ct);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }
}
