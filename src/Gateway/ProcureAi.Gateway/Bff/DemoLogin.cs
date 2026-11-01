using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// One-click demo login (plan 06 ruling 17): <c>GET /bff/demo-login/users</c> lists the configured demo roles and
/// <c>POST /bff/demo-login</c> with <c>{ "user": "tom" }</c> signs that user in. Both answer 404 unless <c>DemoLogin:Enabled</c>.
/// The browser sends a key only; the username and password come from configuration. The normal Keycloak login is unchanged.
/// </summary>
public static class DemoLoginEndpoints
{
    /// <summary>A body larger than this is not a demo login request.</summary>
    private const int MaxBodyBytes = 1024;

    public static IEndpointRouteBuilder MapDemoLogin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bff/demo-login");
        group.MapGet("/users", ListUsers).AllowAnonymous();
        group.MapPost("", LoginAsync).AllowAnonymous().RequireRateLimiting(DemoEntryRateLimit.PolicyName);
        return app;
    }

    private static IResult ListUsers(HttpContext http, IOptions<DemoLoginOptions> options, IAntiforgery antiforgery)
    {
        var demo = options.Value;
        if (!demo.Enabled)
        {
            return NotFound();
        }

        // The welcome page is anonymous: this hands it the XSRF token its POST must carry (like /bff/user does for a session).
        BffEndpoints.IssueXsrfToken(http, antiforgery);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(demo.Users.Select(u => new { key = u.Key, label = u.Label, tenant = u.TenantName }));
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, IOptions<DemoLoginOptions> options, IAntiforgery antiforgery, DemoSignIn signIn)
    {
        if (!options.Value.Enabled)
        {
            return NotFound();
        }

        // Anti-forgery like every other state-changing BFF call, also for an anonymous caller (the XSRF middleware checks
        // session-borne calls only): a login posted from another site must not sign this browser in (login CSRF).
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return XsrfMiddleware.Rejection();
        }

        var user = options.Value.Find(await ReadUserKeyAsync(http.Request));
        if (user is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown demo user");
        }

        return await signIn.SignInAsync(http, user)
            ? Results.NoContent()
            // Nothing of the identity provider's answer reaches the browser; the gateway log has the status.
            : Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Demo login is not available right now");
    }

    /// <summary>The <c>user</c> string of a small JSON object body; null for anything else.</summary>
    private static async Task<string?> ReadUserKeyAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType() || request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[256];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
            {
                if (body.Length + read > MaxBodyBytes)
                {
                    return null;
                }

                body.Write(buffer, 0, read);
            }

            using var json = JsonDocument.Parse(body.ToArray());
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("user", out var user)
                   && user.ValueKind == JsonValueKind.String
                ? user.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The same answer as an unknown <c>/bff</c> path: when off, the feature does not exist.</summary>
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
}

/// <summary>
/// Signs a listed demo user in: the password grant on the separate demo client, then the same session as the code flow's
/// callback. The ID token is validated with the OIDC handler's own validation parameters and the provider's signing keys, the
/// handler's claim actions run on it, the tenant and roles come from the access token (<see cref="AccessTokenClaims"/>), the
/// tokens are saved like <c>SaveTokens</c> does, and the cookie scheme stores the ticket in Redis. Refresh, logout and
/// logout-everywhere then work unchanged; <see cref="TokenRefresher"/> uses the demo client for this session's tokens.
/// </summary>
public sealed class DemoSignIn(
    IOptionsMonitor<OpenIdConnectOptions> oidc, IOptions<DemoLoginOptions> demoLogin, TimeProvider time, ILogger<DemoSignIn> logger)
{
    /// <returns>False when the provider refused or answered something unusable; nothing about it reaches the browser.</returns>
    public async Task<bool> SignInAsync(HttpContext http, DemoLoginUser user)
    {
        if (await AcquireAsync(http, user, user.Tenant) is not { } signIn)
        {
            return false;
        }

        // A browser that already has a session: like the code-flow callback, the cookie handler writes the new ticket under the
        // browser's current session key (the store moves that key out of the previous user's index).
        await http.SignInAsync(BffSchemes.Cookie, signIn.Principal, signIn.Properties);
        logger.LogInformation("Demo login of {DemoUser} created a session", user.Key);
        return true;
    }

    /// <summary>
    /// The password grant for <paramref name="user"/> in <paramref name="tenant"/> (scope <c>organization:&lt;tenant&gt;</c>), checked
    /// like the code flow's callback: the principal and session properties to sign in with, or null when the provider refused, answered
    /// something unusable, or gave a token for another tenant. Used by the demo login and by the tenant switch of a demo session.
    /// </summary>
    public async Task<(ClaimsPrincipal Principal, AuthenticationProperties Properties)?> AcquireAsync(
        HttpContext http, DemoLoginUser user, string tenant)
    {
        var cancellationToken = http.RequestAborted;
        var demo = demoLogin.Value;
        var options = oidc.Get(BffSchemes.Oidc);
        try
        {
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            // The code flow's scopes, with exactly one organization: the token carries exactly that tenant (pre-flight ruling P5).
            var scope = OrganizationScope.For(options.Scope, tenant);
            using var response = await options.Backchannel.PostAsync(
                configuration.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = demo.ClientId,
                    ["client_secret"] = demo.ClientSecret,
                    ["username"] = user.Username,
                    ["password"] = demo.Password,
                    ["scope"] = scope,
                }),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Demo login of {DemoUser} was refused by the identity provider with status {Status}", user.Key, (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            var accessToken = Text(root, "access_token");
            var idToken = Text(root, "id_token");
            var refreshToken = Text(root, "refresh_token");
            if (accessToken is null || idToken is null || refreshToken is null)
            {
                logger.LogWarning("Demo login of {DemoUser}: the identity provider's answer lacks a token", user.Key);
                return null;
            }

            var parameters = options.TokenValidationParameters.Clone();
            parameters.ValidIssuer = configuration.Issuer;
            parameters.ValidAudience = demo.ClientId;
            parameters.ValidAudiences = null;
            parameters.IssuerSigningKeys = configuration.SigningKeys;
            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
            if (!validated.IsValid)
            {
                logger.LogWarning(validated.Exception, "Demo login of {DemoUser}: the ID token is not valid", user.Key);
                return null;
            }

            var identity = validated.ClaimsIdentity;
            // What the OIDC handler does after validating the ID token (no user info call): its claim actions, then our
            // OnTokenValidated (tenant and roles from the access token).
            using (var empty = JsonDocument.Parse("{}"))
            {
                foreach (var action in options.ClaimActions)
                {
                    action.Run(empty.RootElement, identity, options.ClaimsIssuer ?? BffSchemes.Oidc);
                }
            }

            AccessTokenClaims.Apply(identity, accessToken);
            var tenants = identity.FindAll("tenant").Select(c => c.Value).ToArray();
            if (tenants is not [var issued] || !string.Equals(issued, tenant, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Demo login of {DemoUser}: the token's tenant is not the one asked for; no session is made", user.Key);
                return null;
            }

            var properties = new AuthenticationProperties();
            properties.Items[TokenRefresher.ClientItem] = demo.ClientId;
            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var seconds) ? seconds : 60;
            properties.StoreTokens(
            [
                new AuthenticationToken { Name = "access_token", Value = accessToken },
                new AuthenticationToken { Name = "refresh_token", Value = refreshToken },
                new AuthenticationToken { Name = "id_token", Value = idToken },
                new AuthenticationToken { Name = "token_type", Value = Text(root, "token_type") ?? "Bearer" },
                new AuthenticationToken
                {
                    Name = "expires_at",
                    Value = time.GetUtcNow().AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture),
                },
            ]);

            return (new ClaimsPrincipal(identity), properties);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
                                              or SecurityTokenException or ArgumentException
                                              or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            logger.LogWarning(exception, "Demo login of {DemoUser} failed", user.Key);
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
