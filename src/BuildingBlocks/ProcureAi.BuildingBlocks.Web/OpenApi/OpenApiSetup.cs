using Asp.Versioning;
using Microsoft.AspNetCore.Builder;

namespace ProcureAi.BuildingBlocks.Web.OpenApi;

internal static class OpenApiSetup
{
    /// <summary>
    /// Serves one document per API version at <c>/openapi/v1.json</c>. The document services are registered by
    /// <c>AddApiVersioning().AddOpenApi()</c> (see VersioningSetup).
    /// </summary>
    public static WebApplication MapProcureAiOpenApi(this WebApplication app)
    {
        app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous();
        return app;
    }
}
