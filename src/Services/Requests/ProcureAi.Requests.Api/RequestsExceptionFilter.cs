using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Requests.Api;

/// <summary>
/// Turns the application's expected outcomes into problem details inside the MVC pipeline, so [Idempotent] and
/// [CachedGet] see an ordinary response (and share it with waiting callers) instead of an exception:
/// not found (or another tenant's) 404, forbidden 403, stale version 412, broken business rule 422.
/// </summary>
public sealed class RequestsExceptionFilter(ProblemDetailsFactory problems) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            NotFoundException => StatusCodes.Status404NotFound,
            ForbiddenException => StatusCodes.Status403Forbidden,
            ConcurrencyConflictException => StatusCodes.Status412PreconditionFailed,
            DomainException => StatusCodes.Status422UnprocessableEntity,
            _ => 0,
        };
        if (status == 0)
        {
            return;
        }

        var problem = problems.CreateProblemDetails(context.HttpContext, status, title: context.Exception.Message);
        context.Result = new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
