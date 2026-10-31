using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>The tokens a refresh returned. <see cref="RefreshToken"/> and <see cref="IdToken"/> are null when unchanged.</summary>
public sealed record RefreshedTokens(string AccessToken, string? RefreshToken, string? IdToken, TimeSpan ExpiresIn);

/// <summary>The client credentials a session's tokens were issued to (a refresh token only works with its own client).</summary>
public sealed record TokenClient(string ClientId, string ClientSecret);

/// <summary>
/// Talks to the identity provider's token and end-session endpoints from the server, with the credentials of the client that
/// issued the session's tokens: the BFF client (code flow), or the demo login client for a one-click demo session (marked by
/// <see cref="ClientItem"/> in the session's properties). Endpoint addresses come from the discovery document the OIDC handler
/// already uses.
/// </summary>
public sealed class TokenRefresher(
    IOptionsMonitor<OpenIdConnectOptions> oidc, IOptions<DemoLoginOptions> demoLogin, ILogger<TokenRefresher> logger)
{
    /// <summary>Session property naming the client of a session that did not come from the code flow.</summary>
    public const string ClientItem = "procure.client";

    /// <summary>
    /// The client of this session. A demo session whose demo login has since been switched off has none: its tokens are not
    /// refreshed and the session ends (fails closed).
    /// </summary>
    public TokenClient? ClientOf(AuthenticationProperties? properties)
    {
        if (properties is null || !properties.Items.TryGetValue(ClientItem, out var clientId) || clientId is null)
        {
            var options = oidc.Get(BffSchemes.Oidc);
            return new TokenClient(options.ClientId!, options.ClientSecret!);
        }

        var demo = demoLogin.Value;
        return demo.Enabled && string.Equals(clientId, demo.ClientId, StringComparison.Ordinal)
            ? new TokenClient(demo.ClientId, demo.ClientSecret)
            : null;
    }

    public async Task<RefreshedTokens?> RefreshAsync(string refreshToken, AuthenticationProperties? properties, CancellationToken cancellationToken)
    {
        try
        {
            var client = ClientOf(properties);
            if (client is null)
            {
                logger.LogWarning("The session's client is no longer configured; the session ends");
                return null;
            }

            var options = oidc.Get(BffSchemes.Oidc);
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            using var response = await options.Backchannel.PostAsync(
                configuration.TokenEndpoint,
                Form(client, ("grant_type", "refresh_token"), ("refresh_token", refreshToken)),
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
    public async Task EndProviderSessionAsync(string? refreshToken, AuthenticationProperties? properties, CancellationToken cancellationToken)
    {
        var client = ClientOf(properties);
        if (string.IsNullOrEmpty(refreshToken) || client is null)
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
                configuration.EndSessionEndpoint, Form(client, ("refresh_token", refreshToken)), cancellationToken);
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

    private static FormUrlEncodedContent Form(TokenClient client, params (string Key, string Value)[] extra) =>
        new(new[] { ("client_id", client.ClientId), ("client_secret", client.ClientSecret) }
            .Concat(extra)
            .Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));
}
