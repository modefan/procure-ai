using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>The client-credentials login of one OAuth2 client: where, as whom, and how long before expiry a token is replaced.</summary>
public sealed record ClientCredentialsSettings(string TokenUrl, string ClientId, string ClientSecret, TimeSpan RefreshSkew);

/// <summary>
/// Gets a client-credentials access token and keeps it until shortly before it expires
/// (<see cref="ClientCredentialsSettings.RefreshSkew"/>). Callers that find no valid token share one token request, and a
/// failed request is never cached: the next caller tries again. Neither the token nor the secret is logged.
/// One instance per client: this service as <c>procure-internal</c> (the default), or another client such as the
/// Keycloak admin client of the Access service.
/// </summary>
public sealed class ServiceTokenProvider
{
    public const string HttpClientName = "procure-internal-token";

    private readonly IHttpClientFactory _clients;
    private readonly string _httpClientName;
    private readonly Func<ClientCredentialsSettings> _settings;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private string? _token;
    private DateTimeOffset _validUntil;
    private Task<string>? _inFlight;

    /// <summary>This service's own login: the <c>Auth:Internal</c> section.</summary>
    public ServiceTokenProvider(IHttpClientFactory clients, IOptions<InternalClientOptions> options, TimeProvider clock)
        : this(clients, HttpClientName, () => FromOptions(options.Value), clock)
    {
    }

    /// <param name="httpClientName">A named client without any token handler (the token request itself carries no token).</param>
    /// <param name="settings">Read on every token request, so changed configuration applies without a restart.</param>
    public ServiceTokenProvider(
        IHttpClientFactory clients, string httpClientName, Func<ClientCredentialsSettings> settings, TimeProvider clock)
    {
        _clients = clients;
        _httpClientName = httpClientName;
        _settings = settings;
        _clock = clock;
    }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        Task<string> pending;
        lock (_gate)
        {
            if (_token is not null && _clock.GetUtcNow() < _validUntil)
            {
                return Task.FromResult(_token);
            }

            // Not tied to this caller's cancellation: the others waiting on the same request must not lose their token.
            pending = _inFlight ??= RequestAsync();
        }

        return pending.WaitAsync(cancellationToken);
    }

    private async Task<string> RequestAsync()
    {
        try
        {
            // Let the caller's lock release before the first await, so the request never runs under it.
            await Task.Yield();
            var settings = _settings();
            using var http = _clients.CreateClient(_httpClientName);
            using var response = await http.PostAsync(
                settings.TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret,
                }));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>()
                ?? throw new InvalidOperationException("The token endpoint returned an empty response.");
            if (string.IsNullOrEmpty(body.AccessToken))
            {
                throw new InvalidOperationException("The token endpoint returned no access token.");
            }

            lock (_gate)
            {
                _token = body.AccessToken;
                _validUntil = _clock.GetUtcNow().AddSeconds(body.ExpiresIn) - settings.RefreshSkew;
            }

            return body.AccessToken;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }

    private static ClientCredentialsSettings FromOptions(InternalClientOptions options) =>
        new(options.TokenUrl, options.ClientId, options.ClientSecret, options.RefreshSkew);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
