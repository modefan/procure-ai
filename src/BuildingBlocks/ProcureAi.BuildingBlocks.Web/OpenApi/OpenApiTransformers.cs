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
    public const string TenantScheme = "TenantId";

    /// <summary>Adds the security schemes and the write headers to every OpenAPI document of the service.</summary>
    public static IServiceCollection AddProcureAiOpenApiDescriptions(this IServiceCollection services, string serviceName)
    {
        services.AddSingleton<SecuritySchemeTransformer>();
        services.AddSingleton<TenantRequirementTransformer>();
        services.ConfigureAll<OpenApiOptions>(options =>
        {
            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddOperationTransformer(HeaderTransformer.TransformAsync);
            options.AddOperationTransformer<TenantRequirementTransformer>();
        });
        // After the versioning package has named the document, so the title is ours.
        services.PostConfigureAll<OpenApiOptions>(options =>
        {
            options.AddDocumentTransformer((document, context, _) =>
            {
                // Without this the tool that writes the document at build time puts its own name in `info`.
                document.Info.Title = ApiTitle(serviceName);
                document.Info.Description = $"HTTP API of the {serviceName} service of the ProcureAi demo.";
                document.Info.Version = context.DocumentName.StartsWith('v') ? context.DocumentName[1..] + ".0" : context.DocumentName;
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>"requests-api" becomes "ProcureAi Requests API".</summary>
    internal static string ApiTitle(string serviceName) =>
        "ProcureAi " + string.Join(' ', serviceName.Split('-').Select(word =>
            word.Equals("api", StringComparison.OrdinalIgnoreCase) ? "API" : char.ToUpperInvariant(word[0]) + word[1..]));

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
                Description = "The admin API key (Claude's and the operator's access; all permissions in every organisation). "
                    + "Tenant endpoints also need TenantId below.",
            };
            document.Components.SecuritySchemes[TenantScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyDefaults.TenantHeader,
                Description = "Only with the admin API key: the organisation a tenant endpoint acts in, e.g. acme or globex. Sent as "
                    + "X-Tenant-Id on the endpoints that need one (their lock lists TenantId). Without it they answer 400. "
                    + "A Keycloak login (OAuth2) takes the organisation from the token and ignores this header.",
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

    /// <summary>
    /// On <c>[RequireTenant]</c> actions the admin API key needs <c>X-Tenant-Id</c> too, so their own requirement is
    /// <c>ApiKey</c> and <c>TenantId</c> together, or <c>OAuth2</c>. Swagger UI then sends the organisation set once in the
    /// Authorize dialog with every call to them, and with no other call.
    /// </summary>
    internal sealed class TenantRequirementTransformer(IOptions<OpenApiAuthOptions> auth) : IOpenApiOperationTransformer
    {
        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            if (context.Document is not { } document
                || !context.Description.ActionDescriptor.FilterDescriptors.Any(d => d.Filter is RequireTenantAttribute))
            {
                return Task.CompletedTask;
            }

            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(ApiKeyScheme, document)] = [],
                    [new OpenApiSecuritySchemeReference(TenantScheme, document)] = [],
                },
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(OAuth2Scheme, document)] = [.. auth.Value.Scopes.Keys] },
            ];
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
