using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>Hubs</c> section: which browser origins may open a WebSocket to <c>/hubs/**</c> besides the gateway's own. A WebSocket upgrade
/// is a GET, so the anti-forgery header does not protect it, and a SignalR client can skip negotiate; the browser's <c>Origin</c> header
/// is what tells a cross-site page apart.
/// </summary>
public sealed class HubOptions : IValidatableObject
{
    public const string SectionName = "Hubs";

    /// <summary>Extra allowed origins, e.g. <c>https://app.example.com</c> (scheme, host and port only). Empty by default: same origin only.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || uri.GetLeftPart(UriPartial.Authority) != origin.TrimEnd('/'))
            {
                yield return new ValidationResult(
                    $"'{origin}' is not an origin (scheme://host[:port], nothing else).", [nameof(AllowedOrigins)]);
            }
        }
    }
}
