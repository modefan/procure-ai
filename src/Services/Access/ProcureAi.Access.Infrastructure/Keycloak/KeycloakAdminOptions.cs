using System.ComponentModel.DataAnnotations;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Access.Infrastructure.Keycloak;

/// <summary>
/// The <c>Keycloak</c> section: how the Access service reaches the Keycloak admin API. It logs in as the confidential client
/// <c>procure-access-admin</c> (client credentials), the only identity with Keycloak admin rights; the secret comes from user
/// secrets or a Kubernetes secret, and only the Access service has it.
/// </summary>
public sealed class KeycloakAdminOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Keycloak's base address, without the realm (<c>http://localhost:8080</c>).</summary>
    [Required, Url]
    public string AdminBaseUrl { get; set; } = "";

    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string Realm { get; set; } = "";

    /// <summary>The alias of the demo company's organization; it is also the tenant ID of the company.</summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string DemoOrganization { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string AdminClientId { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string AdminClientSecret { get; set; } = "";

    /// <summary>A cached admin token is replaced this long before it expires.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    internal Uri BaseAddress => new(AdminBaseUrl.TrimEnd('/') + "/");

    internal ClientCredentialsSettings ToCredentials() => new(
        $"{AdminBaseUrl.TrimEnd('/')}/realms/{Realm}/protocol/openid-connect/token", AdminClientId, AdminClientSecret, TokenRefreshSkew);
}
