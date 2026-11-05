using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>RateLimiting:DemoEntry</c> section: the <c>demo-entry</c> limit on the anonymous and cheap ways into a session
/// (<c>POST /bff/demo-login</c>, <c>POST /bff/switch-tenant</c>), each of which costs a password grant at Keycloak. Per endpoint: a limit per
/// client (an IPv4 address or an IPv6 /64, as a trusted proxy forwards it) and a global limit over every client, on top of the gateway's
/// general per-IP limit. Many reviewers behind one NAT share a client's allowance, so it is sized for a group (plan 08 carry-over 10); the
/// global limit caps what all clients together can cost Keycloak.
/// </summary>
public sealed class DemoEntryRateLimitOptions : IValidatableObject
{
    public const string SectionName = "RateLimiting:DemoEntry";

    /// <summary>Calls allowed per window, endpoint and client.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 30;

    /// <summary>Calls allowed per window and endpoint over every client together.</summary>
    [Range(1, 10_000_000)]
    public int GlobalPermitLimit { get; set; } = 300;

    /// <summary>The fixed window of both limits.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The IPv6 prefix that counts as one client: a host picks its own interface identifier (the low 64 bits) and can rotate it at will, so
    /// keying on the full address would give one machine unlimited allowances.
    /// </summary>
    [Range(1, 128)]
    public int Ipv6PrefixLength { get; set; } = 64;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (GlobalPermitLimit < PermitLimit)
        {
            yield return new ValidationResult(
                $"{nameof(GlobalPermitLimit)} ({GlobalPermitLimit}) must be at least {nameof(PermitLimit)} ({PermitLimit}).",
                [nameof(GlobalPermitLimit), nameof(PermitLimit)]);
        }
    }
}
