using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>
/// Gets the client-credentials access token of this service and keeps it until shortly before it expires
/// (<see cref="InternalClientOptions.RefreshSkew"/>). Callers that find no valid token share one token request, and a
/// failed request is never cached: the next caller tries again. Neither the token nor the secret is logged.
/// </summary>
public sealed class ServiceTokenProvider(
    IHttpClientFactory clients, IOptions<InternalClientOptions> options, TimeProvider clock)
{
    public const string HttpClientName = "procure-internal-token";

    private readonly Lock _gate = new();
    private string? _token;
    private DateTimeOffset _validUntil;
    private Task<string>? _inFlight;

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        Task<string> pending;
        lock (_gate)
        {
            if (_token is not null && clock.GetUtcNow() < _validUntil)
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
            var settings = options.Value;
            using var http = clients.CreateClient(HttpClientName);
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
                _validUntil = clock.GetUtcNow().AddSeconds(body.ExpiresIn) - settings.RefreshSkew;
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
