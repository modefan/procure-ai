using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.BuildingBlocks.Web.Errors;

public static class ProblemDetailsSetup
{
    /// <summary>
    /// ProblemDetails for every error: DomainException becomes 422, model validation 400
    /// (from [ApiController]), anything else a generic 500.
    /// </summary>
    public static IServiceCollection AddProcureAiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<DomainExceptionHandler>();
        // A DomainException our handler dealt with is an expected outcome, not an unhandled error to log.
        services.Configure<ExceptionHandlerOptions>(options =>
            options.SuppressDiagnosticsCallback = context =>
                context.ExceptionHandledBy == ExceptionHandledType.ExceptionHandlerService && context.Exception is DomainException);
        return services;
    }
}
