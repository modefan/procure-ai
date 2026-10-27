using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Purchasing.Infrastructure;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>One call Purchasing made to the supplier API, as it went out and as it was answered.</summary>
public sealed record SupplierCall(
    string Method, string Path, string? ExternalReference, string? BuyerReference, int Status, string? SupplierOrderId, bool Blocked, JsonElement? Body);

/// <summary>
/// Purchasing's way to the supplier in the tests: forwards every call to the in-process simulator and records it. A test can
/// block the orders of a tenant (answered 503 here, never reaching the supplier, so the order stays waiting without a supplier
/// reference) or hold the 202 of one order until something has happened (the supplier's webhook beating our own commit).
/// </summary>
public sealed class SupplierTraffic(Func<HttpMessageHandler> supplier) : HttpMessageHandler
{
    private readonly ConcurrentQueue<SupplierCall> _calls = new();

    public IReadOnlyList<SupplierCall> Calls => [.. _calls];

    /// <summary>Tenants whose order submissions are answered 503 here.</summary>
    public ConcurrentDictionary<string, bool> BlockedTenants { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// By tenant: awaited (with our order ID) after the supplier accepted an order of the tenant, before Purchasing sees the 202.
    /// Used once, then removed.
    /// </summary>
    public ConcurrentDictionary<string, Func<Guid, Task>> HoldAfterAccepted { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<SupplierCall> SubmissionsOf(Guid orderId) =>
        [.. _calls.Where(c => c.Method == "POST" && c.ExternalReference == orderId.ToString())];

    public IReadOnlyList<SupplierCall> CancelsOf(string supplierReference) =>
        [.. _calls.Where(c => c.Path.EndsWith($"/{supplierReference}/cancel", StringComparison.Ordinal))];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonElement? body = null;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var copy = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Content = copy;
            body = bytes.Length > 0 ? JsonDocument.Parse(bytes).RootElement.Clone() : null;
        }

        var external = Text(body, "externalReference");
        var buyer = Text(body, "buyerReference");
        var path = request.RequestUri!.AbsolutePath;
        if (buyer is not null && BlockedTenants.Keys.Any(t => buyer.StartsWith(t + "/", StringComparison.Ordinal)))
        {
            _calls.Enqueue(new SupplierCall(request.Method.Method, path, external, buyer, 503, null, true, body));
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        using var invoker = new HttpMessageInvoker(supplier(), disposeHandler: false);
        var response = await invoker.SendAsync(request, cancellationToken);
        string? supplierOrderId = null;
        if (response.StatusCode == HttpStatusCode.Accepted && response.Content is not null)
        {
            var answer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            supplierOrderId = Text(JsonDocument.Parse(answer).RootElement, "supplierOrderId");
            var content = new ByteArrayContent(answer);
            foreach (var header in response.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = content;
        }

        _calls.Enqueue(new SupplierCall(request.Method.Method, path, external, buyer, (int)response.StatusCode, supplierOrderId, false, body));
        var tenant = buyer?.Split('/')[0];
        if (response.StatusCode == HttpStatusCode.Accepted && tenant is not null && Guid.TryParse(external, out var orderId)
            && HoldAfterAccepted.TryRemove(tenant, out var hold))
        {
            await hold(orderId);
        }

        return response;
    }

    private static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Switches the tests can flip inside the running Purchasing API.</summary>
public sealed class TestHooks
{
    /// <summary>Tenants whose next order numbers are handed out slowly, so two creates of one request both pass the "exists?" check.</summary>
    public ConcurrentDictionary<string, TimeSpan> SlowSequences { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tenants whose handlers pause when they queue their cache eviction, which the cancel handler does after deciding and before
    /// saving: a receipt can commit in between, and the cancel then loses on the order's version.
    /// </summary>
    public ConcurrentDictionary<string, TimeSpan> SlowEvictions { get; } = new(StringComparer.Ordinal);
}

/// <summary>The real tenant sequence, slowed down for the tenants a test names.</summary>
public sealed class DelayedTenantSequence(TenantSequence<PurchasingDbContext> inner, TestHooks hooks) : ITenantSequence
{
    public async Task<int> NextAsync(string tenantId, string name, CancellationToken cancellationToken)
    {
        if (hooks.SlowSequences.TryGetValue(tenantId, out var delay))
        {
            await Task.Delay(delay, cancellationToken);
        }

        return await inner.NextAsync(tenantId, name, cancellationToken);
    }
}

/// <summary>The real invalidator, slowed down when it is handed the tags of an order of a tenant a test names.</summary>
public sealed class HookedInvalidator(MessageCacheInvalidator inner, TestHooks hooks) : ICacheInvalidator
{
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) =>
        inner.RemoveByTagAsync(tags, cancellationToken);

    public async Task RemoveAfterCommitAsync(IEnumerable<string> tags)
    {
        tags = [.. tags];
        foreach (var (tenant, delay) in hooks.SlowEvictions)
        {
            if (tags.Any(t => t.Contains($":{tenant}:", StringComparison.Ordinal)))
            {
                await Task.Delay(delay);
                break;
            }
        }

        await inner.RemoveAfterCommitAsync(tags);
    }
}
