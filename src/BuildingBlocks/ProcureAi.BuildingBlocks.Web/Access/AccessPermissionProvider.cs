using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// The permissions of a caller, from the Access service: the admin API key has all of them without asking anyone; a caller
/// without a tenant has none; everyone else gets what Access says for their roles. When Access cannot be reached and
/// nothing is cached, the answer is "no permissions" (the request is 403), never an error.
/// </summary>
public sealed class AccessPermissionProvider(IAccessDirectory access, ILogger<AccessPermissionProvider> logger) : IPermissionProvider
{
    public async ValueTask<IReadOnlySet<string>> GetPermissionsAsync(ICurrentUser user, CancellationToken ct)
    {
        if (user.IsAdminApiKey)
        {
            return Permissions.All;
        }

        if (user.TenantId is not { } tenant)
        {
            return new HashSet<string>();
        }

        try
        {
            var effective = await access.GetEffectiveAccessAsync(user.UserId, [.. user.Roles], tenant, ct);
            return effective.Permissions.ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Circuit open, timeout, connection refused, a 5xx: all mean the same to the caller, so deny.
            logger.LogWarning(exception, "Access could not be reached for tenant {TenantId}; the caller gets no permissions", tenant);
            return new HashSet<string>();
        }
    }
}
