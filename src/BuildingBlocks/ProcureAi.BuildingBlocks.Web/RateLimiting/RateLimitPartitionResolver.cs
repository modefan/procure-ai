using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.RateLimiting;

/// <summary>Chooses the rate-limit partition of a request.</summary>
internal sealed class RateLimitPartitionResolver(RateLimitPartitioning partitioning, ApiKeyValidator? apiKeys)
{
    private const string UnknownClient = "unknown";
    private const string AdminKeyPartition = "admin-api-key";

    /// <returns>The partition key, and whether the request carries the correct admin API key.</returns>
    public (string Partition, bool IsAdminKey) Resolve(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
        if (partitioning == RateLimitPartitioning.ClientIp || apiKeys is null)
        {
            return ($"ip:{ip}", false);
        }

        // The admin API key has its own partition. Only a correct key gets it, so wrong keys
        // cannot use up the real key's allowance; they count against their IP like anyone else.
        if (apiKeys.IsValid(context.Request))
        {
            return (AdminKeyPartition, true);
        }

        // Authentication runs before the limiter, so a signed-in caller is known here. The sub claim is the user,
        // the tenant claim the tenant; both come from a validated token, never from a header.
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.FindFirst("sub")?.Value is { Length: > 0 } sub)
        {
            return ($"user:{user.FindFirst("tenant")?.Value}:{sub}", false);
        }

        return ($"ip:{ip}", false);
    }
}
