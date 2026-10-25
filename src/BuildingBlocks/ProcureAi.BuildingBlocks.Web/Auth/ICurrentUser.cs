namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>The caller of the current request: a JWT user or the admin API key.</summary>
public interface ICurrentUser
{
    string UserId { get; }

    /// <summary>Tenant from the token claim <c>tenant</c>, or from <c>X-Tenant-Id</c> for the admin API key.</summary>
    string? TenantId { get; }

    IReadOnlySet<string> Roles { get; }

    bool IsAdminApiKey { get; }

    string? CallingService { get; }

    string? OnBehalfOfUser { get; }
}
