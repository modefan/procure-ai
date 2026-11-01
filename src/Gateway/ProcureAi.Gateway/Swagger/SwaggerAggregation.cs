using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Swagger;

/// <summary>
/// One Swagger UI for all services. It only serves the UI; each service's OpenAPI document is proxied from the
/// service (see the <c>ReverseProxy</c> section). The Authorize dialog comes from the security schemes in those
/// documents: <c>ApiKey</c>, and OAuth2 with Keycloak, which here uses the public <c>procure-swagger</c> client with PKCE.
/// </summary>
public static class SwaggerAggregation
{
    public static IServiceCollection AddGatewaySwagger(this IServiceCollection services, IConfiguration configuration) =>
        services.AddValidatedOptions<SwaggerOptions>(configuration, SwaggerOptions.SectionName);

    public static WebApplication UseGatewaySwagger(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<SwaggerOptions>>().Value;
        app.UseSwaggerUI(ui =>
        {
            ui.RoutePrefix = "swagger";
            ui.DocumentTitle = "Procure AI APIs";
            foreach (var definition in options.Definitions)
            {
                ui.SwaggerEndpoint(definition.Url, definition.Name);
            }

            ui.OAuthClientId(options.OAuthClientId);
            ui.OAuthScopes(options.OAuthScopes);
            ui.OAuthUsePkce();
        });
        return app;
    }
}
