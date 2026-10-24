using Asp.Versioning;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Versioning;

internal static class VersioningSetup
{
    /// <summary>Version in the URL segment (<c>/api/v1/...</c>), default 1.0; OpenAPI groups are named <c>v1</c>.</summary>
    public static IServiceCollection AddProcureAiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi();
        return services;
    }
}
