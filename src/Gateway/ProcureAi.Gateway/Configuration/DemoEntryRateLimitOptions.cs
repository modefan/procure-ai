using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>RateLimiting:DemoEntry</c> section: the <c>demo-entry</c> limit on the anonymous and cheap ways into a session
/// (<c>POST /bff/demo-login</c>, <c>POST /bff/switch-tenant</c>), each of which costs a password grant at Keycloak. Per client IP (as
/// a trusted proxy forwards it), per endpoint, on top of the gateway's global per-IP limit.
/// </summary>
public sealed class DemoEntryRateLimitOptions
{
    public const string SectionName = "RateLimiting:DemoEntry";

    /// <summary>Calls allowed per window, endpoint and client IP.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 10;

    /// <summary>The fixed window.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
