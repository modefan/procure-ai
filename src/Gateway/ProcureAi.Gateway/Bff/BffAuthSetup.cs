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
        services.AddSingleton<SwitchTicketStore>();

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
                // Chunked like the default; a tenant switch can make the handler ignore the request's cookie (a new session key).
                options.CookieManager = new SessionCookieManager();
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

                // A tenant switch: the same user, exactly the tenant asked for, or no new session (the old one stays).
                if (context.Principal is { } principal && TenantSwitchEndpoints.Refusal(context.Properties!, principal) is { } refusal)
                {
                    // The failure handler does not get the properties of a failed validation: it finds the way back here.
                    context.HttpContext.Items[TenantSwitchEndpoints.RefusedReturnItem] = BffEndpoints.SafeReturnUrl(context.Properties!.RedirectUri);
                    context.Fail($"Tenant switch refused: {refusal}");
                }

                return Task.CompletedTask;
            },
            OnTicketReceived = async context =>
            {
                if (context.Properties is not { } properties || !properties.Items.ContainsKey(TenantSwitchEndpoints.TenantItem))
                {
                    return;
                }

                // A tenant switch replaces the session: the new ticket gets a new key (the cookie handler must not see a request cookie that
                // a same-site callback may carry), and the old ticket leaves the store and the user's index.
                context.HttpContext.Items[SessionRotation.HideRequestCookieItem] = true;
                if (properties.Items.TryGetValue(TenantSwitchEndpoints.SessionItem, out var old) && !string.IsNullOrEmpty(old))
                {
                    await context.HttpContext.RequestServices.GetRequiredService<RedisTicketStore>().RemoveAsync(old, context.HttpContext.RequestAborted);
                }

                TenantSwitchEndpoints.Forget(properties);
            },
            OnRedirectToIdentityProvider = context =>
            {
                // /bff/register asks for prompt=create, which opens the provider's registration form.
                if (context.Properties.Items.TryGetValue(PromptItem, out var prompt))
                {
                    context.ProtocolMessage.Prompt = prompt;
                }

                // A tenant switch asks for exactly one organization, silently unless this is the visible retry.
                if (context.Properties.Items.TryGetValue(TenantSwitchEndpoints.TenantItem, out var tenant) && tenant is not null)
                {
                    context.ProtocolMessage.Scope = OrganizationScope.For(context.Options.Scope, tenant);
                    if (!context.Properties.Items.ContainsKey(TenantSwitchEndpoints.InteractiveItem))
                    {
                        context.ProtocolMessage.Prompt = "none";
                    }
                }

                return Task.CompletedTask;
            },
            OnRemoteFailure = async context =>
            {
                // A silent switch the provider could not answer without the user (its own session ended): ask once more, visibly. The
                // state carries what the retry needs; the session cookie does not come along on this cross-site redirect.
                if (context.Properties is { } properties && properties.Items.ContainsKey(TenantSwitchEndpoints.TenantItem)
                    && !properties.Items.ContainsKey(TenantSwitchEndpoints.InteractiveItem)
                    && context.Failure?.Data["error"] is "login_required" or "interaction_required" or "consent_required" or "account_selection_required")
                {
                    logger.LogInformation("Tenant switch: the identity provider needs the user; asking again with its login page");
                    var retry = new AuthenticationProperties { RedirectUri = BffEndpoints.SafeReturnUrl(properties.RedirectUri) };
                    foreach (var item in new[] { TenantSwitchEndpoints.TenantItem, TenantSwitchEndpoints.SubjectItem, TenantSwitchEndpoints.SessionItem })
                    {
                        if (properties.Items.TryGetValue(item, out var value))
                        {
                            retry.Items[item] = value;
                        }
                    }

                    retry.Items[TenantSwitchEndpoints.InteractiveItem] = "true";
                    await context.HttpContext.ChallengeAsync(BffSchemes.Oidc, retry);
                    context.HandleResponse();
                    return;
                }

                // The user cancelled, or the provider refused: back to the app, not an error page (a refused switch keeps the old session
                // and goes back to where it started).
                logger.LogWarning(context.Failure, "Sign-in at the identity provider failed");
                var back = context.HttpContext.Items[TenantSwitchEndpoints.RefusedReturnItem] as string
                           ?? (context.Properties?.Items.ContainsKey(TenantSwitchEndpoints.TenantItem) == true
                               ? BffEndpoints.SafeReturnUrl(context.Properties.RedirectUri)
                               : "/");
                context.Response.Redirect(back);
                context.HandleResponse();
            },
        };
    }
}
