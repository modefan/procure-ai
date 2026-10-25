using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace ProcureAi.Gateway.Bff;

/// <summary>The tokens a refresh returned. <see cref="RefreshToken"/> and <see cref="IdToken"/> are null when unchanged.</summary>
public sealed record RefreshedTokens(string AccessToken, string? RefreshToken, string? IdToken, TimeSpan ExpiresIn);

/// <summary>
/// Talks to the identity provider's token and end-session endpoints from the server, with the gateway's client
/// credentials. Endpoint addresses come from the discovery document the OIDC handler already uses.
/// </summary>
public sealed class TokenRefresher(IOptionsMonitor<OpenIdConnectOptions> oidc, ILogger<TokenRefresher> logger)
{
    public async Task<RefreshedTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var options = oidc.Get(BffSchemes.Oidc);
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            using var response = await options.Backchannel.PostAsync(
                configuration.TokenEndpoint,
                Form(options, ("grant_type", "refresh_token"), ("refresh_token", refreshToken)),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Token refresh was refused with status {Status}; the session ends", (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            var access = root.GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(access))
            {
                return null;
            }

            return new RefreshedTokens(
                access,
                root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
                root.TryGetProperty("id_token", out var id) ? id.GetString() : null,
                TimeSpan.FromSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetDouble() : 60));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Token refresh failed; the session ends");
            return null;
        }
    }

    /// <summary>Ends the user's single sign-on session at the identity provider (back channel, no browser involved).</summary>
    public async Task EndProviderSessionAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        try
        {
            var options = oidc.Get(BffSchemes.Oidc);
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            if (string.IsNullOrEmpty(configuration.EndSessionEndpoint))
            {
                return;
            }

            using var response = await options.Backchannel.PostAsync(
                configuration.EndSessionEndpoint, Form(options, ("refresh_token", refreshToken)), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The identity provider did not end its session; status {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The gateway session is removed regardless; the provider session ends by itself when it times out.
            logger.LogWarning(exception, "Could not reach the identity provider to end its session");
        }
    }

    public static DateTimeOffset? ParseExpiresAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private static FormUrlEncodedContent Form(OpenIdConnectOptions options, params (string Key, string Value)[] extra) =>
        new(new[] { ("client_id", options.ClientId!), ("client_secret", options.ClientSecret!) }
            .Concat(extra)
            .Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));
}
