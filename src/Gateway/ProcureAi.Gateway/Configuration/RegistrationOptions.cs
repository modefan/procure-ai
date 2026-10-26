using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>The <c>Registration</c> section: where self-registered users land.</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>The tenant (organization alias) of the demo company. Must be the one Access is configured with.</summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string DemoTenantId { get; set; } = "demo";
}
