namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>The caller of the current request: a JWT user, a service account or the admin API key.</summary>
public interface ICurrentUser
{
    /// <summary>False when there is no HTTP request or it is anonymous.</summary>
    bool IsAuthenticated { get; }

    string UserId { get; }

    /// <summary>Tenant from the token claim <c>tenant</c>, or from <c>X-Tenant-Id</c> for the admin API key.</summary>
    string? TenantId { get; }

    IReadOnlySet<string> Roles { get; }

    bool IsAdminApiKey { get; }

    /// <summary>A token issued to one of our own services (<c>azp</c> equals <c>Auth:InternalClientId</c>).</summary>
    bool IsServiceAccount { get; }

    /// <summary>The <c>X-Calling-Service</c> header; only read for the admin API key and service accounts.</summary>
    string? CallingService { get; }

    /// <summary>The <c>X-On-Behalf-Of-User</c> header; only read for the admin API key and service accounts.</summary>
    string? OnBehalfOfUser { get; }
}
