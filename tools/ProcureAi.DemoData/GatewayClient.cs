using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ProcureAi.DemoData;

/// <summary>
/// Calls the public APIs through the gateway the way a demo user would, with the one admin API key acting on behalf of that user
/// (<c>X-On-Behalf-Of-User</c>) in a tenant (<c>X-Tenant-Id</c>), as plan 06 ruling 1 decided. The services then act as that user:
/// requester, approver eligibility, separation of duties, payment rules and the audit all see the demo user, not the key. Every POST carries
/// the caller's Idempotency-Key, so a retried or repeated step is executed once.
/// </summary>
public sealed class GatewayClient(HttpClient http, IOptions<DemoDataOptions> options)
{
    public const string CallingService = "demo-data";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Sends one call and returns the status and body; never throws for an HTTP status.</summary>
    public async Task<GatewayResponse> SendAsync(
        HttpMethod method, string tenant, string actAs, string path, object? body = null, uint? ifMatch = null, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Api-Key", options.Value.AdminApiKey);
        message.Headers.Add("X-Calling-Service", CallingService);
        message.Headers.Add("X-Tenant-Id", tenant);
        message.Headers.Add("X-On-Behalf-Of-User", actAs);
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is { } version)
        {
            message.Headers.TryAddWithoutValidation("If-Match", $"\"{version.ToString(CultureInfo.InvariantCulture)}\"");
        }

        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await http.SendAsync(message, cancellationToken);
        return new GatewayResponse(method, path, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Reads one resource; throws <see cref="GatewayException"/> unless the answer is 200.</summary>
    public async Task<T> GetAsync<T>(string tenant, string actAs, string path, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, tenant, actAs, path, cancellationToken: cancellationToken)).Read<T>(HttpStatusCode.OK);
}

public sealed record GatewayResponse(HttpMethod Method, string Path, HttpStatusCode Status, string Body)
{
    /// <summary>The body as <typeparamref name="T"/> when the status is one of <paramref name="expected"/>; otherwise a <see cref="GatewayException"/>.</summary>
    public T Read<T>(params HttpStatusCode[] expected) =>
        expected.Contains(Status)
            ? JsonSerializer.Deserialize<T>(Body, GatewayClient.Json) ?? throw new GatewayException(this, "empty body")
            : throw new GatewayException(this, $"expected {string.Join(" or ", expected.Select(s => (int)s))}");
}

/// <summary>An answer the seeder did not expect. Transient ones (a service starting, a broker blip, rate limiting) are waited out.</summary>
public sealed class GatewayException(GatewayResponse response, string expectation)
    : Exception($"{response.Method} {response.Path}: {expectation}, got {(int)response.Status}: {Truncate(response.Body)}")
{
    public HttpStatusCode Status { get; } = response.Status;

    public bool IsTransient => Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string Truncate(string body) => body.Length <= 500 ? body : body[..500] + "...";
}
