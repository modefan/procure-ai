using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>DemoLogin</c> section: one-click sign-in as a listed demo user (plan 06 ruling 17). Off by default; the AppHost and the
/// demo deployment switch it on. When on, the gateway signs the user in with the password grant on its own confidential client
/// (<see cref="ClientId"/>, direct access grants only), never on the BFF client. Everything is checked at start when enabled;
/// when off nothing else is required and the endpoints answer 404.
/// </summary>
public sealed partial class DemoLoginOptions : IValidatableObject
{
    public const string SectionName = "DemoLogin";

    public bool Enabled { get; set; }

    /// <summary>The Keycloak client with direct access grants. Not the BFF client: that one keeps the code flow only.</summary>
    public string ClientId { get; set; } = "procure-demo-login";

    /// <summary>The secret of <see cref="ClientId"/>. From user secrets or a Kubernetes secret, never from the repo.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>The demo users' password (the realm's <c>${KC_DEMO_PASSWORD}</c>). From configuration, never from the browser.</summary>
    public string Password { get; set; } = "";

    /// <summary>The only users that may be signed in this way. The browser posts a <see cref="DemoLoginUser.Key"/>, nothing else.</summary>
    public List<DemoLoginUser> Users { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            yield return new ValidationResult("DemoLogin:ClientId is required when demo login is enabled.", [nameof(ClientId)]);
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return new ValidationResult("DemoLogin:ClientSecret is required when demo login is enabled.", [nameof(ClientSecret)]);
        }

        if (string.IsNullOrEmpty(Password))
        {
            yield return new ValidationResult("DemoLogin:Password is required when demo login is enabled.", [nameof(Password)]);
        }

        if (Users.Count == 0)
        {
            yield return new ValidationResult("DemoLogin:Users must list at least one user when demo login is enabled.", [nameof(Users)]);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Users.Count; i++)
        {
            var user = Users[i];
            var at = $"DemoLogin:Users:{i}";
            if (user.Key is null || !KeyPattern().IsMatch(user.Key))
            {
                yield return new ValidationResult($"{at}:Key must be 1-32 lowercase letters, digits or '-'.", [nameof(Users)]);
            }
            else if (!keys.Add(user.Key))
            {
                yield return new ValidationResult($"{at}:Key '{user.Key}' is listed twice.", [nameof(Users)]);
            }

            if (string.IsNullOrWhiteSpace(user.Username))
            {
                yield return new ValidationResult($"{at}:Username is required.", [nameof(Users)]);
            }

            if (string.IsNullOrWhiteSpace(user.Label))
            {
                yield return new ValidationResult($"{at}:Label is required.", [nameof(Users)]);
            }

            if (user.Tenant is null || !TenantPattern().IsMatch(user.Tenant))
            {
                yield return new ValidationResult($"{at}:Tenant must be an organization alias.", [nameof(Users)]);
            }

            if (string.IsNullOrWhiteSpace(user.TenantName))
            {
                yield return new ValidationResult($"{at}:TenantName is required.", [nameof(Users)]);
            }
        }
    }

    /// <summary>The listed user with exactly this key (ordinal: no case folding, no trimming), or null.</summary>
    public DemoLoginUser? Find(string? key) =>
        key is null ? null : Users.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex TenantPattern();
}

/// <summary>One demo user the welcome page offers.</summary>
public sealed class DemoLoginUser
{
    /// <summary>What the browser posts, e.g. <c>tom</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>The Keycloak username, e.g. <c>tom@acme.test</c>. Never sent to the browser.</summary>
    public string Username { get; set; } = "";

    /// <summary>The button text, e.g. "Requester (Tom)".</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// The organization alias (tenant ID) the login asks for with <c>scope=organization:&lt;tenant&gt;</c>; the token's
    /// <c>tenant</c> claim must be exactly this, or no session is made (pre-flight ruling P5: every login yields one tenant).
    /// </summary>
    public string Tenant { get; set; } = "";

    /// <summary>The company name shown under the button, e.g. "Acme Corp".</summary>
    public string TenantName { get; set; } = "";
}
