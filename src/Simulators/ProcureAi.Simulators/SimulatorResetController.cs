using Microsoft.AspNetCore.Mvc;
using ProcureAi.Simulators.Chaos;
using ProcureAi.Simulators.Erp;
using ProcureAi.Simulators.Supplier;
using ProcureAi.Simulators.Webhooks;

namespace ProcureAi.Simulators;

/// <summary>
/// The simulators' part of the nightly demo reset (plan 06 ruling 4), called first by ProcureAi.DemoData with the admin key: the supplier
/// forgets its orders, the ERP its postings, both number sequences start again (SO-1001, INV-1001, ERP-5000), every webhook still waiting is
/// dropped, and chaos is back to Normal. Safe to call twice.
/// <para>
/// Only there when <c>DemoReset:Enabled</c> is true (<see cref="SimulatorDemoResetOptions"/>, the services' switch): otherwise the controller
/// is left out (<see cref="DemoResetControllerSwitch"/>) and the route is 404 for everyone.
/// </para>
/// </summary>
[ApiController]
[RequireAdminKey]
[Route("api/v1/simulators/reset")]
public sealed class SimulatorResetController(
    SupplierStore supplier, ErpStore erp, WebhookQueue webhooks, ChaosState chaos, ILogger<SimulatorResetController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Reset()
    {
        webhooks.Reset();
        supplier.Reset();
        erp.Reset();
        chaos.Reset();
        logger.LogWarning("Demo reset: supplier orders, ERP postings and waiting webhooks dropped, chaos Normal");
        return NoContent();
    }
}
