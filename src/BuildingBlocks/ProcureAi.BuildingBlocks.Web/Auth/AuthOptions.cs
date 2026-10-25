using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>The <c>Auth</c> configuration section. Switching to Entra ID is a change of these values only.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC authority (issuer) the JWTs are validated against.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Authority { get; set; } = "";

    /// <summary>Audience the JWTs must be issued for.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = "";

    /// <summary>Require HTTPS for the authority's metadata. Turn off only for a local http Keycloak.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>SHA-256 (64 hex characters) of the admin API key. Empty disables the API key.</summary>
    [RegularExpression(@"^([0-9a-fA-F]{64})?\z", ErrorMessage = "AdminApiKeySha256 must be 64 hexadecimal characters or empty.")]
    public string AdminApiKeySha256 { get; set; } = "";
}
