namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Answers which permissions a user has. Plan 02 replaces the default with a client of the Access service.</summary>
public interface IPermissionProvider
{
    ValueTask<IReadOnlySet<string>> GetPermissionsAsync(ICurrentUser user, CancellationToken ct);
}
