using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;
using StackExchange.Redis;

namespace ProcureAi.Gateway.Bff;

internal static class BffAuthSetup
{
    public const string SessionPolicy = "Session";
    public const string PromptItem = "procure.prompt";

    /// <summary>
    /// Login with the identity provider (code flow with PKCE), the Redis-backed session cookie, a pass-through for
    /// calls that bring their own credentials, anti-forgery, and Data Protection keys shared through Redis so that
    /// every gateway instance reads every cookie.
    /// </summary>
    public static IServiceCollection AddBffAuthentication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RedisTicketStore>();
        services.AddSingleton<TokenRefresher>();
        services.AddSingleton<SessionCookieEvents>();

        services.AddDataProtection().SetApplicationName("procure-gateway");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IConnectionMultiplexer, IOptions<BffSessionOptions>>((keys, redis, session) =>
                keys.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(), session.Value.RedisKeyPrefix + "data-protection-keys"));

        services.AddAntiforgery(options =>
        {
            options.HeaderName = XsrfMiddleware.HeaderName;
            options.Cookie.Name = "__Host-procure-xsrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = BffSchemes.Smart;
                options.DefaultChallengeScheme = BffSchemes.Smart;
            })
            .AddPolicyScheme(BffSchemes.Smart, displayName: null, options =>
                options.ForwardDefaultSelector = context =>
                    BffSchemes.UsesPassThrough(context.Request) ? BffSchemes.PassThrough : BffSchemes.Cookie)
            .AddScheme<AuthenticationSchemeOptions, PassThroughAuthenticationHandler>(BffSchemes.PassThrough, null)
            .AddCookie(BffSchemes.Cookie, options =>
            {
                options.Cookie.Name = BffSchemes.SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.SlidingExpiration = true;
                options.EventsType = typeof(SessionCookieEvents);
            })
            .AddOpenIdConnect(BffSchemes.Oidc, _ => { });

        services.AddOptions<CookieAuthenticationOptions>(BffSchemes.Cookie)
            .Configure<RedisTicketStore, IOptions<BffSessionOptions>>((cookie, store, session) =>
            {
                cookie.SessionStore = store;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(session.Value.IdleTimeoutMinutes);
            });

        services.AddOptions<OpenIdConnectOptions>(BffSchemes.Oidc)
            .Configure<IOptions<OidcOptions>, ILogger<OidcOptions>>(ConfigureOidc);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            // The /bff endpoints work on the session only; an API key or bearer header never opens them.
            .AddPolicy(SessionPolicy, policy => policy.AddAuthenticationSchemes(BffSchemes.Cookie).RequireAuthenticatedUser());
        return services;
    }

    private static void ConfigureOidc(OpenIdConnectOptions options, IOptions<OidcOptions> settings, ILogger<OidcOptions> logger)
    {
        var oidc = settings.Value;
        options.SignInScheme = BffSchemes.Cookie;
        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.ResponseType = "code";
        // The code comes back in the query string of a redirect (the default is a form post).
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = false;
        // Keep claim names as issued (sub, name, roles, tenant).
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.Scope.Clear();
        foreach (var scope in oidc.Scopes)
        {
            options.Scope.Add(scope);
        }

        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity && context.TokenEndpointResponse?.AccessToken is { Length: > 0 } token)
                {
                    AccessTokenClaims.Apply(identity, token);
                }

                return Task.CompletedTask;
            },
            OnRedirectToIdentityProvider = context =>
            {
                // /bff/register asks for prompt=create, which opens the provider's registration form.
                if (context.Properties.Items.TryGetValue(PromptItem, out var prompt))
                {
                    context.ProtocolMessage.Prompt = prompt;
                }

                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                // The user cancelled, or the provider refused: back to the app, not an error page.
                logger.LogWarning(context.Failure, "Sign-in at the identity provider failed");
                context.Response.Redirect("/");
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }
}
