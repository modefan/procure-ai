namespace ProcureAi.BuildingBlocks.Web.RateLimiting;

/// <summary>Who shares a rate-limit allowance.</summary>
public enum RateLimitPartitioning
{
    /// <summary>
    /// Services: an authenticated user within their tenant, the admin API key its own partition, anonymous callers
    /// their client IP. Behind the gateway every call has the gateway's connection, so the IP alone would throttle
    /// all users as one.
    /// </summary>
    UserAndTenant,

    /// <summary>The gateway: the client IP (as forwarded by a trusted proxy, if any).</summary>
    ClientIp,
}
