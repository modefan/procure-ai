using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>Swagger</c> section: one Swagger UI for all services. A new service is configuration only: a route and
/// cluster in <c>ReverseProxy</c> and one entry in <see cref="Definitions"/>.
/// </summary>
public sealed class SwaggerOptions : IValidatableObject
{
    public const string SectionName = "Swagger";

    /// <summary>The public client of Swagger UI (authorization code flow with PKCE).</summary>
    [Required(AllowEmptyStrings = false)]
    public string OAuthClientId { get; set; } = "procure-swagger";

    /// <summary>
    /// The scopes Swagger UI's login asks for. <c>organization</c> makes Keycloak put the chosen company in the token: without it a
    /// user of several companies gets no tenant.
    /// </summary>
    [Required, MinLength(1)]
    public string[] OAuthScopes { get; set; } = ["openid", "profile", "organization"];

    [Required, MinLength(1)]
    public SwaggerDefinition[] Definitions { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var definition in Definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                yield return new ValidationResult("A Swagger definition needs a Name.", [nameof(Definitions)]);
            }

            if (!definition.Url.StartsWith('/') || definition.Url.StartsWith("//", StringComparison.Ordinal))
            {
                yield return new ValidationResult($"Swagger definition '{definition.Name}': Url must be a path on the gateway, starting with '/'.", [nameof(Definitions)]);
            }
        }
    }
}

/// <summary>One entry of the Swagger UI dropdown.</summary>
public sealed class SwaggerDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Gateway path of the service's OpenAPI document, proxied to the service.</summary>
    public string Url { get; set; } = "";
}
