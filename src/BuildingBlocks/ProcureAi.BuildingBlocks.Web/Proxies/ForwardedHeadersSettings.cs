using System.ComponentModel.DataAnnotations;
using System.Net;

namespace ProcureAi.BuildingBlocks.Web.Proxies;

/// <summary>
/// The <c>ForwardedHeaders</c> configuration section: which reverse proxies (the gateway, an ingress) may tell a
/// service the real client address through <c>X-Forwarded-For</c>. Empty means no proxy is trusted and the header
/// is ignored, so a caller cannot choose its own rate-limit partition by sending it.
/// </summary>
public sealed class ForwardedHeadersSettings : IValidatableObject
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Addresses of trusted proxies, e.g. <c>10.0.0.5</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Trusted proxy networks in CIDR notation, e.g. <c>10.42.0.0/16</c> (the cluster's pod network).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>How many proxies in a chain are processed.</summary>
    [Range(1, 10)]
    public int ForwardLimit { get; set; } = 1;

    public bool HasTrustedProxies => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    public IEnumerable<IPAddress> ParsedProxies => KnownProxies.Select(IPAddress.Parse);

    public IEnumerable<IPNetwork> ParsedNetworks => KnownNetworks.Select(IPNetwork.Parse);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var proxy in KnownProxies.Where(p => !IPAddress.TryParse(p, out _)))
        {
            yield return new ValidationResult($"'{proxy}' is not an IP address.", [nameof(KnownProxies)]);
        }

        foreach (var network in KnownNetworks.Where(n => !IPNetwork.TryParse(n, out _)))
        {
            yield return new ValidationResult($"'{network}' is not a network in CIDR notation.", [nameof(KnownNetworks)]);
        }
    }
}
