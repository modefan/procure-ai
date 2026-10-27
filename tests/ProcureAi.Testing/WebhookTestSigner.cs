using ProcureAi.BuildingBlocks.Web.Webhooks;

namespace ProcureAi.Testing;

/// <summary>Signs a test request the way a webhook sender does.</summary>
public static class WebhookTestSigner
{
    public static HttpRequestMessage Sign(HttpRequestMessage request, string secret, DateTimeOffset at)
    {
        var body = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];
        request.Headers.Remove(WebhookSignature.HeaderName);
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.CreateHeader(secret, at.ToUnixTimeSeconds(), body));
        return request;
    }
}
