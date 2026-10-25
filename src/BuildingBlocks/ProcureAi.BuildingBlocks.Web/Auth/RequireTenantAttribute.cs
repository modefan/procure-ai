using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>
/// The endpoint needs a tenant: from the token, or from <c>X-Tenant-Id</c> for the admin API key. Without one: 400 for the
/// API key (send the header), 403 for a user token (it has no tenant to act in).
/// </summary>
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

        // The admin API key can fix this by sending the header (400). A token without a tenant claim cannot: 403.
        var (status, message) = user.IsAdminApiKey
            ? (StatusCodes.Status400BadRequest, "X-Tenant-Id header is required")
            : (StatusCodes.Status403Forbidden, "The token has no tenant.");
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = message, Detail = message })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
