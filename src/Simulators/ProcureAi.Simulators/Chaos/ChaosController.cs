using Microsoft.AspNetCore.Mvc;

namespace ProcureAi.Simulators.Chaos;

/// <summary>The chaos switch (admin key): how slow and how broken the supplier and the ERP are.</summary>
[ApiController]
[RequireAdminKey]
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
