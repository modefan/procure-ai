using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>The endpoint needs a tenant: from the token, or from <c>X-Tenant-Id</c> for the admin API key.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTenantAttribute : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (user.TenantId is not null)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "X-Tenant-Id header is required",
            Detail = "X-Tenant-Id header is required",
        };
        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
