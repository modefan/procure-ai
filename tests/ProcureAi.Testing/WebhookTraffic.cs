using System.Collections.Concurrent;
using System.Text.Json;
using ProcureAi.BuildingBlocks.Web.Webhooks;

namespace ProcureAi.Testing;

/// <summary>One webhook the simulator delivered to a service, with the service's answer.</summary>
public sealed record WebhookDelivery(byte[] Body, string? Signature, int Status)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? ExternalReference => Json.TryGetProperty("externalReference", out var value) ? value.GetString() : null;

    public string? Type => Json.TryGetProperty("type", out var value) ? value.GetString() : null;
}

/// <summary>
/// The simulator's way to a service (its webhooks): forwards to the service's test server, bound late (the server is created after
/// the simulator), and records each delivery.
/// </summary>
public sealed class WebhookTraffic(Func<HttpMessageHandler> receiver) : HttpMessageHandler
{
    private readonly ConcurrentQueue<WebhookDelivery> _deliveries = new();

    public IReadOnlyList<WebhookDelivery> Deliveries => [.. _deliveries];

    public IReadOnlyList<WebhookDelivery> Of(Guid orderId) => [.. _deliveries.Where(d => d.ExternalReference == orderId.ToString())];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Headers.TryGetValues(WebhookSignature.HeaderName, out var signatures);
        using var invoker = new HttpMessageInvoker(receiver(), disposeHandler: false);
        var response = await invoker.SendAsync(request, cancellationToken);
        _deliveries.Enqueue(new WebhookDelivery(body, signatures?.SingleOrDefault(), (int)response.StatusCode));
        return response;
    }
}
