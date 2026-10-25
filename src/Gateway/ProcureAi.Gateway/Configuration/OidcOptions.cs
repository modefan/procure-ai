using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>Oidc</c> section: the gateway's own client at the identity provider. Switching from Keycloak to
/// Entra ID changes these values only.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    [Required(AllowEmptyStrings = false)]
    public string Authority { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; set; } = "";

    /// <summary>The client secret. Comes from user secrets or a Kubernetes secret, never from the repo.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ClientSecret { get; set; } = "";

    [Required, MinLength(1)]
    public string[] Scopes { get; set; } = [];

    /// <summary>Require HTTPS for the authority's metadata. Turn off only for a local http Keycloak.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
