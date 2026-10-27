using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Requests.Application.Abstractions;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>The <c>SuppliersClient</c> section: retries and timeouts of the call to Suppliers, and how long catalog answers are cached.</summary>
public sealed class SuppliersClientOptions : ServiceClientResilienceOptions
{
    public const string SectionName = "SuppliersClient";

    /// <summary>How long a catalog answer is served from the cache (a price change reaches new lines after this).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the last good answer may be served while Suppliers cannot be reached.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan FailSafeMaxDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>After a failed refresh, the stale answer is served this long before Suppliers is asked again.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan FailSafeThrottleDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long "no such supplier" is remembered (it is never served from fail-safe).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan NotFoundCacheDuration { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Asks the Suppliers service (<c>GET api/v1/suppliers/internal/{code}/items?skus=...</c>, as this service with its
/// client-credentials token, for the request's tenant) and caches the answers in FusionCache: identical concurrent lookups share
/// one call, and the last good answer is served while Suppliers is unreachable (fail-safe). A failed call is not cached; it throws
/// the <see cref="HttpRequestException"/> or Polly rejection the API answers with 503 and <c>Retry-After</c>.
/// </summary>
public sealed class SuppliersClient(HttpClient http, IFusionCache cache, IOptions<SuppliersClientOptions> options) : ISupplierCatalog
{
    /// <summary>The service name of Suppliers: its address (<c>http://suppliers-api</c>) through service discovery.</summary>
    public const string SuppliersServiceName = "suppliers-api";

    public async Task<SupplierItems?> GetItemsAsync(
        string supplierCode, IReadOnlyCollection<string> skus, string tenantId, CancellationToken cancellationToken)
    {
        var sorted = skus.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var url = $"api/v1/suppliers/internal/{Uri.EscapeDataString(supplierCode)}/items?skus="
                  + string.Join(',', sorted.Select(Uri.EscapeDataString));
        // Every part is escaped, so no value can contain a separator and make two different lookups share a key.
        var key = $"suppliers:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(supplierCode)}:"
                  + string.Join(',', sorted.Select(Uri.EscapeDataString));
        var entry = new FusionCacheEntryOptions
        {
            Duration = options.Value.CacheDuration,
            DistributedCacheDuration = options.Value.CacheDuration,
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = options.Value.FailSafeMaxDuration,
            FailSafeThrottleDuration = options.Value.FailSafeThrottleDuration,
        };

        var answer = await cache.GetOrSetAsync<CatalogAnswer?>(
            key,
            async (context, token) =>
            {
                var found = await GetAsync(url, tenantId, token);
                if (found is null)
                {
                    // "No such supplier" is an answer, not a failure: kept only briefly, and never replaced by a stale supplier.
                    context.Options.Duration = options.Value.NotFoundCacheDuration;
                    context.Options.DistributedCacheDuration = options.Value.NotFoundCacheDuration;
                    context.Options.IsFailSafeEnabled = false;
                }

                return found;
            },
            options: entry,
            token: cancellationToken);
        return answer is null
            ? null
            : new SupplierItems(
                answer.Code, answer.Name, answer.Active,
                answer.Items.ToDictionary(i => i.Sku, i => new CatalogPrice(i.Description, i.UnitPriceExclVat, i.Active), StringComparer.Ordinal));
    }

    private async Task<CatalogAnswer?> GetAsync(string url, string tenantId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ServiceTokenHandler.TenantOption, tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (DependencyFailures.IsTransientStatus(response.StatusCode))
        {
            response.EnsureSuccessStatusCode();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Suppliers answered {(int)response.StatusCode} to a catalog lookup.");
        }

        return await response.Content.ReadFromJsonAsync<CatalogAnswer>(cancellationToken)
            ?? throw new InvalidOperationException("Suppliers returned an empty catalog answer.");
    }

    /// <summary>The part of Suppliers' <c>CatalogItemsDto</c> this client needs (cached as it is, so it must serialize).</summary>
    public sealed record CatalogAnswer(string Code, string Name, bool Active, IReadOnlyList<CatalogAnswerItem> Items);

    public sealed record CatalogAnswerItem(string Sku, string Description, decimal UnitPriceExclVat, bool Active);
}
