using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ProcureAi.Simulators;

/// <summary>
/// Section <c>DemoReset</c>, the same switch the services read (<c>DemoReset__Enabled</c>; the simulators reference no BuildingBlocks, so
/// they have their own copy of the one setting they need). Off by default: <see cref="SimulatorResetController"/> is not mapped and
/// <c>POST api/v1/simulators/reset</c> is 404 for everyone (fail closed), so a deployment whose services have the reset off cannot lose its
/// supplier orders and SO-/INV-/ERP- numbering to a stray reset either.
/// </summary>
public sealed class SimulatorDemoResetOptions
{
    public const string Section = "DemoReset";

    public bool Enabled { get; set; }
}

/// <summary>Leaves <see cref="SimulatorResetController"/> out of the application's controllers unless <c>DemoReset:Enabled</c> is true.</summary>
internal sealed class DemoResetControllerSwitch(bool enabled) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (!enabled)
        {
            feature.Controllers.Remove(typeof(SimulatorResetController).GetTypeInfo());
        }
    }
}
