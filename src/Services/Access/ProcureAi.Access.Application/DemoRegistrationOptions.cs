using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Access.Application;

/// <summary>The <c>DemoRegistration</c> section: what a self-registered user of the demo company gets.</summary>
public sealed class DemoRegistrationOptions
{
    public const string SectionName = "DemoRegistration";

    /// <summary>
    /// The cost center every self-registered user may raise requests on: a sandbox with its own budget, so sign-ups from the
    /// public demo can try the flow without touching another department's budget. It must exist in the demo tenant (the demo
    /// seed creates it in Access and its budget in Budgets).
    /// </summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,50}$")]
    public string SandboxCostCenter { get; set; } = "";
}
