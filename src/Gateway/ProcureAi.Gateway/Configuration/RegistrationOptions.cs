using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>The <c>Registration</c> section: whether self-registered users join the demo company, and which tenant that is.</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>
    /// Whether a signed-in user without a tenant is joined to the demo company (see <c>DemoRegistration</c>). Off by default (plan 08
    /// Task 12c review M4): the realm does not allow self-registration and the web app hides "Register" (its <c>registration.enabled</c>),
    /// so the only tenantless users left are admin users, such as the owner's own Keycloak user for Argo CD, who must not become tenant
    /// users of the demo by signing in to the app. Turn it on together with the realm's <c>registrationAllowed</c> and the web switch.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The tenant (organization alias) of the demo company. Must be the one Access is configured with.</summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string DemoTenantId { get; set; } = "demo";
}
