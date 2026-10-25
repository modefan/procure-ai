using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// The <c>Auth:Internal</c> section: how this service logs in as itself (OAuth2 client credentials) to call other services.
/// One Keycloak client, <c>procure-internal</c>, serves every service; the secret comes from user secrets or a Kubernetes secret.
/// </summary>
public sealed class InternalClientOptions
{
    public const string SectionName = "Auth:Internal";

    /// <summary>The identity provider's token endpoint.</summary>
    [Required, Url]
    public string TokenUrl { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string ClientSecret { get; set; } = "";

    /// <summary>A cached token is replaced this long before it expires, so a call never carries a token about to lapse.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(30);
}
