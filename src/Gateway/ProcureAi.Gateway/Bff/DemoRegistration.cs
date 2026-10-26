using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Moves a signed-in user who belongs to no tenant (just self-registered) into the demo company: asks Access to register
/// them, then refreshes the session's tokens at the identity provider so the new <c>tenant</c> and roles claims appear.
/// Never throws: any failure leaves the session as it was, and the caller answers as for a user without a tenant.
/// </summary>
public sealed class DemoRegistration(
    IRegistrationClient registrations, TokenRefresher refresher, TimeProvider time, ILogger<DemoRegistration> logger)
{
    /// <returns>True when the session now carries the new tokens (and <c>http.User</c> the new claims).</returns>
    public async Task<bool> TryJoinAsync(HttpContext http)
    {
        var cancellationToken = http.RequestAborted;
        try
        {
            var principal = http.User;
            var userId = principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value ?? principal.FindFirst("preferred_username")?.Value;
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(email))
            {
                logger.LogWarning("A signed-in user without a tenant has no subject or email in the token; not registered");
                return false;
            }

            var name = principal.FindFirst("name")?.Value ?? "";
            if (!await registrations.RegisterAsync(userId, email, name, cancellationToken))
            {
                logger.LogWarning("Access did not register user {UserId} in the demo company", userId);
                return false;
            }

            var authenticated = await http.AuthenticateAsync(BffSchemes.Cookie);
            var properties = authenticated.Properties;
            var refreshToken = properties?.GetTokenValue("refresh_token");
            if (authenticated.Principal is null || properties is null || string.IsNullOrEmpty(refreshToken))
            {
                logger.LogWarning("User {UserId} joined the demo company but the session has no refresh token", userId);
                return false;
            }

            var tokens = await refresher.RefreshAsync(refreshToken, cancellationToken);
            if (tokens is null)
            {
                logger.LogWarning("User {UserId} joined the demo company but the tokens could not be refreshed", userId);
                return false;
            }

            ApplyTokens(properties, tokens, time.GetUtcNow());
            var identity = (ClaimsIdentity)authenticated.Principal.Identity!;
            AccessTokenClaims.Apply(identity, tokens.AccessToken);
            await http.SignInAsync(BffSchemes.Cookie, authenticated.Principal, properties);
            http.User = authenticated.Principal;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Registering the signed-in user in the demo company failed");
            return false;
        }
    }

    /// <summary>Stores refreshed tokens in the session's properties (also used by the cookie's refresh before expiry).</summary>
    public static void ApplyTokens(AuthenticationProperties properties, RefreshedTokens tokens, DateTimeOffset now)
    {
        properties.UpdateTokenValue("access_token", tokens.AccessToken);
        if (tokens.RefreshToken is not null)
        {
            properties.UpdateTokenValue("refresh_token", tokens.RefreshToken);
        }

        if (tokens.IdToken is not null)
        {
            properties.UpdateTokenValue("id_token", tokens.IdToken);
        }

        properties.UpdateTokenValue("expires_at", now.Add(tokens.ExpiresIn).ToString("o", CultureInfo.InvariantCulture));
    }
}
