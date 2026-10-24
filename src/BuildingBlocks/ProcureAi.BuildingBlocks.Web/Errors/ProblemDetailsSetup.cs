using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Errors;

internal static class ProblemDetailsSetup
{
    /// <summary>
    /// ProblemDetails for every error: DomainException becomes 422, model validation 400
    /// (from [ApiController]), anything else a generic 500.
    /// </summary>
    public static IServiceCollection AddProcureAiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<DomainExceptionHandler>();
        return services;
    }
}
