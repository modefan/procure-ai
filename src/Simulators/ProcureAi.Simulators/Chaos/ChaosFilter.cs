using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcureAi.Simulators.Chaos;

/// <summary>Inbound calls to this controller suffer the chaos settings of the target system (latency, 503s).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ChaosAttribute : TypeFilterAttribute
{
    public ChaosAttribute(ChaosTarget target) : base(typeof(ChaosFilter)) => Arguments = [target];
}

/// <summary>
/// A resource filter: it runs after the key check and before the request is read, so a "down" system does not even look at the body.
/// Delays use the <see cref="TimeProvider"/> so tests control them.
/// </summary>
internal sealed class ChaosFilter(ChaosTarget target, ChaosState chaos, TimeProvider time) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var decision = chaos.Decide(target);
        if (decision.Latency > TimeSpan.Zero)
        {
            await Task.Delay(decision.Latency, time, context.HttpContext.RequestAborted);
        }

        if (decision.Fail)
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service unavailable",
                Detail = $"The simulated {target.ToString().ToLowerInvariant()} is failing.",
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        await next();
    }
}
