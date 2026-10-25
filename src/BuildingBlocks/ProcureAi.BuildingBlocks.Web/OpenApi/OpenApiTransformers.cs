using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;

namespace ProcureAi.BuildingBlocks.Web.OpenApi;

internal static class OpenApiTransformers
{
    public const string ApiKeyScheme = "ApiKey";
    public const string OAuth2Scheme = "OAuth2";

    /// <summary>Adds the two security schemes and the write headers to every OpenAPI document of the service.</summary>
    public static IServiceCollection AddProcureAiOpenApiDescriptions(this IServiceCollection services)
    {
        services.AddSingleton<SecuritySchemeTransformer>();
        services.ConfigureAll<OpenApiOptions>(options =>
        {
            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddOperationTransformer(HeaderTransformer.TransformAsync);
        });
        return services;
    }

    /// <summary>
    /// <c>ApiKey</c> (the <c>X-Api-Key</c> header, Claude's admin key) and <c>OAuth2</c> (authorization code with PKCE
    /// with the URLs and scopes of <c>Auth:OpenApi</c>). Either one satisfies an operation.
    /// </summary>
    internal sealed class SecuritySchemeTransformer(IOptions<OpenApiAuthOptions> auth) : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            var login = auth.Value;
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[ApiKeyScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyDefaults.KeyHeader,
                Description = "The admin API key (Claude's testing access). Send X-Tenant-Id with it on tenant endpoints.",
            };
            document.Components.SecuritySchemes[OAuth2Scheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Keycloak login: authorization code flow with PKCE.",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri(login.AuthorizationUrl),
                        TokenUrl = new Uri(login.TokenUrl),
                        Scopes = new Dictionary<string, string>(login.Scopes),
                    },
                },
            };

            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeyScheme, document)] = [] });
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(OAuth2Scheme, document)] = [.. login.Scopes.Keys] });
            return Task.CompletedTask;
        }
    }

    internal static class HeaderTransformer
    {
        /// <summary>Documents <c>Idempotency-Key</c> on <c>[Idempotent]</c> actions and <c>If-Match</c> where it is required.</summary>
        public static Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            var filters = context.Description.ActionDescriptor.FilterDescriptors.Select(d => d.Filter).ToList();
            if (filters.OfType<IdempotentAttribute>().Any())
            {
                Add(operation, IdempotentAttribute.HeaderName, "A unique value per submission (e.g. a GUID). Repeating it replays the first result.");
            }

            if (filters.OfType<RequireIfMatchAttribute>().Any())
            {
                Add(operation, RequireIfMatchAttribute.HeaderName, "The ETag of the version you last read, e.g. \"3\". Missing: 428; stale: 412.");
            }

            return Task.CompletedTask;
        }

        private static void Add(OpenApiOperation operation, string name, string description)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Header,
                Required = true,
                Description = description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }
    }
}
