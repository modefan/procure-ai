using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Web.Http;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// Asks the Access service (as this service, with its client-credentials token) and caches the answers in FusionCache:
/// identical concurrent lookups share one call, the last good answer is served while Access is unreachable (fail-safe),
/// and every entry carries the tenant's tag so <c>AccessChanged</c> can evict it. The tenant is always the argument, never
/// the caller's, because system work (a saga timeout) has none. A failed call is not cached; it throws.
/// </summary>
public sealed class AccessClient(
    HttpClient http, IFusionCache cache, IOptions<AccessClientOptions> options) : IAccessDirectory
{
    private const string EffectivePath = "api/v1/access/internal/effective";

    public async ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
        string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken)
    {
        // Sorted, so the order of the roles in the token never splits the cache.
        var sorted = roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var query = string.Concat(sorted.Select(role => $"&roles={Uri.EscapeDataString(role)}"));
        var url = $"{EffectivePath}?userId={Uri.EscapeDataString(userId)}{query}";
        return await cache.GetOrSetAsync<EffectiveAccess>(
            $"access:eff:{tenantId}:{userId}:{string.Join(',', sorted)}",
            async (_, token) => await GetAsync<EffectiveAccess>(url, tenantId, token),
            options: EntryOptions(),
            tags: [AccessCacheTag.Tenant(tenantId)],
            token: cancellationToken);
    }

    public async ValueTask<CostCenterApprovers> GetApproversAsync(
        string costCenterCode, string tenantId, CancellationToken cancellationToken)
    {
        var url = $"api/v1/access/internal/cost-centers/{Uri.EscapeDataString(costCenterCode)}/approvers";
        return await cache.GetOrSetAsync<CostCenterApprovers>(
            $"access:appr:{tenantId}:{costCenterCode}",
            async (_, token) => await GetAsync<CostCenterApprovers>(url, tenantId, token),
            options: EntryOptions(),
            tags: [AccessCacheTag.Tenant(tenantId)],
            token: cancellationToken);
    }

    private FusionCacheEntryOptions EntryOptions() => new()
    {
        Duration = options.Value.CacheDuration,
        // The same in Redis: an instance that reads it from there must not treat it as fresh for longer.
        DistributedCacheDuration = options.Value.CacheDuration,
        IsFailSafeEnabled = true,
        FailSafeMaxDuration = options.Value.FailSafeMaxDuration,
        FailSafeThrottleDuration = options.Value.FailSafeThrottleDuration,
    };

    private async Task<T> GetAsync<T>(string url, string tenantId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ServiceTokenHandler.TenantOption, tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("The Access service returned an empty response.");
    }
}
