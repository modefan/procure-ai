using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Errors;

/// <summary>
/// Turns the application's expected outcomes into problem details inside the MVC pipeline, so [Idempotent] and
/// [CachedGet] see an ordinary response (and share it with waiting callers) instead of an exception:
/// not found (or another tenant's) 404, forbidden 403, no longer in a state that allows it 409, stale version 412,
/// broken business rule 422, and a failed or refused call to another service (<see cref="DependencyFailures"/>) 503 with
/// <c>Retry-After</c>.
/// </summary>
public sealed class ApplicationExceptionFilter(ProblemDetailsFactory problems, IOptions<ErrorResponseOptions> options) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (DependencyFailures.IsTransient(context.Exception))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                options.Value.DependencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            context.Result = Problem(context, StatusCodes.Status503ServiceUnavailable, "A service this one depends on is not available. Try again shortly.");
            context.ExceptionHandled = true;
            return;
        }

        var status = context.Exception switch
        {
            NotFoundException => StatusCodes.Status404NotFound,
            ForbiddenException => StatusCodes.Status403Forbidden,
            ConcurrencyConflictException => StatusCodes.Status412PreconditionFailed,
            ConflictException => StatusCodes.Status409Conflict,
            DomainException => StatusCodes.Status422UnprocessableEntity,
            _ => 0,
        };
        if (status == 0)
        {
            return;
        }

        context.Result = Problem(context, status, context.Exception.Message);
        context.ExceptionHandled = true;
    }

    private ObjectResult Problem(ExceptionContext context, int status, string title) =>
        new(problems.CreateProblemDetails(context.HttpContext, status, title: title))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
}
