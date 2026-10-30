using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// <see cref="ICallerPermissions"/> through the same <see cref="IPermissionProvider"/> the endpoint policies ask (the cached Access client;
/// no permissions when Access cannot answer), for the signed-in caller of this request.
/// </summary>
internal sealed class CallerPermissions(ICurrentUser user, IPermissionProvider permissions) : ICallerPermissions
{
    public async ValueTask<bool> HasAsync(string permission, CancellationToken cancellationToken) =>
        (await permissions.GetPermissionsAsync(user, cancellationToken)).Contains(permission);
}
