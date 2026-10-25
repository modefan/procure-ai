namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Answers which permissions a user has. Services answer it through the Access client (see AccessClientSetup); Access itself from its own database.</summary>
public interface IPermissionProvider
{
    ValueTask<IReadOnlySet<string>> GetPermissionsAsync(ICurrentUser user, CancellationToken ct);
}
