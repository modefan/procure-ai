using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Errors;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>The expected outcomes of the application layer become problem details inside the MVC pipeline.</summary>
public sealed class ApplicationExceptionFilterTests
{
    [Fact]
    public void NotFound_is_404() => Handle(new NotFoundException("gone")).StatusCode.ShouldBe(StatusCodes.Status404NotFound);

    [Fact]
    public void Concurrency_conflict_is_412() =>
        Handle(new ConcurrencyConflictException("stale")).StatusCode.ShouldBe(StatusCodes.Status412PreconditionFailed);

    [Fact]
    public void Forbidden_is_403() => Handle(new ForbiddenException("no")).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

    [Fact]
    public void Broken_domain_rule_is_422() =>
        Handle(new DomainException("rule")).StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);

    [Fact]
    public void Result_is_problem_details_with_the_message_as_title()
    {
        var result = Handle(new NotFoundException("Request PR-1 was not found."));

        result.ContentTypes.ShouldContain("application/problem+json");
        var problem = result.Value.ShouldBeOfType<ProblemDetails>();
        problem.Status.ShouldBe(StatusCodes.Status404NotFound);
        problem.Title.ShouldBe("Request PR-1 was not found.");
    }

    [Fact]
    public void Other_exceptions_are_left_to_the_global_handler()
    {
        var context = Context(new InvalidOperationException("boom"));

        new ApplicationExceptionFilter(Factory()).OnException(context);

        context.ExceptionHandled.ShouldBeFalse();
        context.Result.ShouldBeNull();
    }

    private static ObjectResult Handle(Exception exception)
    {
        var context = Context(exception);
        new ApplicationExceptionFilter(Factory()).OnException(context);
        context.ExceptionHandled.ShouldBeTrue();
        return context.Result.ShouldBeOfType<ObjectResult>();
    }

    private static ExceptionContext Context(Exception exception) =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = exception,
        };

    private static ProblemDetailsFactory Factory() =>
        new DefaultProblemDetailsFactory(Options.Create(new ApiBehaviorOptions()), Options.Create(new ProblemDetailsOptions()));
}
