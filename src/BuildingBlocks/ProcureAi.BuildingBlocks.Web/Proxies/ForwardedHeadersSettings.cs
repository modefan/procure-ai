using System.ComponentModel.DataAnnotations;
using System.Net;
using ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;

namespace ProcureAi.BuildingBlocks.Web.Proxies;

/// <summary>
/// The <c>ForwardedHeaders</c> configuration section: which reverse proxies (the gateway, an ingress) may tell an app
/// the scheme the browser used (<c>X-Forwarded-Proto</c>) and, where <see cref="Headers"/> says so, the real client address
/// (<c>X-Forwarded-For</c>). Empty proxy lists mean no proxy is trusted and the headers are ignored, so a caller cannot
/// choose its own rate-limit partition by sending them.
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

    /// <summary>
    /// Which forwarded headers the trusted proxies may set: <c>XForwardedProto</c> (the default: the services, so their
    /// OpenAPI documents name the https address the browser used) or <c>XForwardedFor, XForwardedProto</c> (the gateway,
    /// behind the ingress, whose rate limits need the visitor's address). A service never takes the client address from a
    /// header: any pod that may call it could claim any address (plan 08 ruling 5), while a forged scheme changes nothing
    /// but the server URL in that caller's own OpenAPI document.
    /// </summary>
    public ForwardedHeaders Headers { get; set; } = ForwardedHeaders.XForwardedProto;

    public bool HasTrustedProxies => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    public IEnumerable<IPAddress> ParsedProxies => KnownProxies.Select(IPAddress.Parse);

    public IEnumerable<IPNetwork> ParsedNetworks => KnownNetworks.Select(IPNetwork.Parse);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        const ForwardedHeaders allowed = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (Headers == ForwardedHeaders.None || (Headers & ~allowed) != 0)
        {
            yield return new ValidationResult(
                $"'{Headers}' is not allowed: use XForwardedProto, or XForwardedFor, XForwardedProto.", [nameof(Headers)]);
        }

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
