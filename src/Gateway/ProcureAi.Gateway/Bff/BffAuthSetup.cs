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
                CopyAccessTokenClaims(context);
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

    /// <summary>
    /// The tenant and roles live in the access token (that is what the services read). They are copied onto the
    /// session's identity once, at sign-in, so the gateway can answer /bff/user and compute permissions from them.
    /// The token comes straight from the provider's token endpoint; it is read here, not trusted from a client.
    /// </summary>
    private static void CopyAccessTokenClaims(TokenValidatedContext context)
    {
        var accessToken = context.TokenEndpointResponse?.AccessToken;
        if (string.IsNullOrEmpty(accessToken) || context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        foreach (var existing in identity.FindAll("tenant").ToList())
        {
            identity.RemoveClaim(existing);
        }

        if (token.TryGetClaim("tenant", out var tenant))
        {
            identity.AddClaim(new Claim("tenant", tenant.Value));
        }

        var known = identity.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var role in token.Claims.Where(c => c.Type == "roles" && known.Add(c.Value)))
        {
            identity.AddClaim(new Claim("roles", role.Value));
        }
    }
}
