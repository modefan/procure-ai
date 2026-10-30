using Microsoft.AspNetCore.Mvc;

namespace ProcureAi.Simulators.Chaos;

/// <summary>
/// The chaos switch: how slow and how broken the supplier and the ERP are. The admin key (Claude, tests) or the simulators' key (Purchasing's
/// "break the supplier API" button on the status screen, plan 06 ruling 11). A mode other than Normal restores itself after
/// <c>Simulators:Chaos:AutoRestoreAfter</c>; <c>restoresAt</c> in the answer says when.
/// </summary>
[ApiController]
[RequireAdminOrSimulatorKey]
[Route("api/v1/simulators/chaos")]
public sealed class ChaosController(ChaosState chaos) : ControllerBase
{
    [HttpGet]
    public ChaosConfiguration Get() => chaos.Current;

    [HttpPut]
    public ActionResult<ChaosConfiguration> Put(ChaosConfiguration configuration)
    {
        foreach (var (property, message) in configuration.Validate())
        {
            ModelState.AddModelError(property, message);
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        chaos.Set(configuration);
        return chaos.Current;
    }
}
