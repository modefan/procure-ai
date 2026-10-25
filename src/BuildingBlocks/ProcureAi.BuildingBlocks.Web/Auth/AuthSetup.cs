using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Auth;

internal static class AuthSetup
{
    /// <summary>
    /// Smart policy scheme (API key header present: API key, otherwise JWT), permission policies and a fallback
    /// policy that denies anonymous callers unless an endpoint says <c>[AllowAnonymous]</c>.
    /// </summary>
    public static IServiceCollection AddProcureAiAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<AuthOptions>(configuration, AuthOptions.SectionName);
        services.AddValidatedOptions<OpenApiAuthOptions>(configuration, OpenApiAuthOptions.SectionName);
        services.AddSingleton<ApiKeyValidator>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = ApiKeyDefaults.SmartScheme;
                options.DefaultChallengeScheme = ApiKeyDefaults.SmartScheme;
            })
            .AddPolicyScheme(ApiKeyDefaults.SmartScheme, displayName: null, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyDefaults.KeyHeader)
                        ? ApiKeyDefaults.Scheme
                        : ApiKeyDefaults.BearerScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null)
            .AddJwtBearer(ApiKeyDefaults.BearerScheme, _ => { });

        services.AddOptions<JwtBearerOptions>(ApiKeyDefaults.BearerScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.Authority = auth.Value.Authority;
                jwt.Audience = auth.Value.Audience;
                jwt.RequireHttpsMetadata = auth.Value.RequireHttpsMetadata;
                // Keep claim names as issued (sub, roles, tenant) instead of mapping them to long URIs.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "sub";
                jwt.TokenValidationParameters.RoleClaimType = "roles";
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(InternalPolicy.Name, policy => policy.RequireAuthenticatedUser().AddRequirements(new InternalRequirement()));
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, InternalAuthorizationHandler>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }
}
