using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Web.Http;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// Asks the Access service (as this service, with its client-credentials token) and caches the answers in FusionCache:
/// identical concurrent lookups share one call, the last good answer is served while Access is unreachable (fail-safe),
/// and every entry carries the tenant's tag and generation (see <see cref="AccessCacheGeneration"/>) so <c>AccessChanged</c>
/// can drop them. The tenant is always the argument, never the caller's, because system work (a saga timeout) has none.
/// A failed call is not cached; it throws.
/// </summary>
public sealed class AccessClient(
    HttpClient http, IFusionCache cache, AccessCacheGeneration generations, IOptions<AccessClientOptions> options)
    : IAccessDirectory
{
    private const string EffectivePath = "api/v1/access/internal/effective";

    public async ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
        string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken)
    {
        // Sorted, so the order of the roles in the token never splits the cache.
        var sorted = roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var query = string.Concat(sorted.Select(role => $"&roles={Uri.EscapeDataString(role)}"));
        var url = $"{EffectivePath}?userId={Uri.EscapeDataString(userId)}{query}";
        var generation = await generations.GetAsync(tenantId, cancellationToken);
        return await cache.GetOrSetAsync<EffectiveAccess>(
            Key("eff", tenantId, generation, userId, string.Join(',', sorted.Select(Uri.EscapeDataString))),
            async (_, token) => await GetAsync<EffectiveAccess>(url, tenantId, token)
                ?? throw new InvalidOperationException("The Access service returned an empty response."),
            options: EntryOptions(),
            tags: [AccessCacheTag.Tenant(tenantId)],
            token: cancellationToken);
    }

    public async ValueTask<CostCenterApprovers> GetApproversAsync(
        string costCenterCode, string tenantId, CancellationToken cancellationToken)
    {
        var url = $"api/v1/access/internal/cost-centers/{Uri.EscapeDataString(costCenterCode)}/approvers";
        var generation = await generations.GetAsync(tenantId, cancellationToken);
        var found = await cache.GetOrSetAsync<CostCenterApprovers?>(
            Key("appr", tenantId, generation, costCenterCode),
            async (context, token) =>
            {
                var approvers = await GetAsync<CostCenterApprovers>(url, tenantId, token, allowNotFound: true);
                if (approvers is null)
                {
                    // "No such cost center" is an answer, not a failure: kept only briefly, and never replaced by stale approvers.
                    context.Options.Duration = options.Value.NotFoundCacheDuration;
                    context.Options.DistributedCacheDuration = options.Value.NotFoundCacheDuration;
                    context.Options.IsFailSafeEnabled = false;
                }

                return approvers;
            },
            options: EntryOptions(),
            tags: [AccessCacheTag.Tenant(tenantId)],
            token: cancellationToken);
        return found ?? throw new NotFoundException($"Cost center '{costCenterCode}' does not exist.");
    }

    public async ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
        (await GetUserAsync(userId, tenantId, cancellationToken))?.DisplayName;

    public async ValueTask<DirectoryUser?> GetUserAsync(string userId, string tenantId, CancellationToken cancellationToken)
    {
        var url = $"api/v1/access/internal/users/{Uri.EscapeDataString(userId)}";
        var generation = await generations.GetAsync(tenantId, cancellationToken);
        // "user", not the earlier "name": entries cached before the email was part of the answer are never read as having none.
        var found = await cache.GetOrSetAsync<UserEntry?>(
            Key("user", tenantId, generation, userId),
            async (context, token) =>
            {
                var name = await GetAsync<UserEntry>(url, tenantId, token, allowNotFound: true);
                if (name is null)
                {
                    // "No such user" is an answer: kept only briefly, and never replaced by a stale name.
                    context.Options.Duration = options.Value.NotFoundCacheDuration;
                    context.Options.DistributedCacheDuration = options.Value.NotFoundCacheDuration;
                    context.Options.IsFailSafeEnabled = false;
                }

                return name;
            },
            options: EntryOptions(),
            tags: [AccessCacheTag.Tenant(tenantId)],
            token: cancellationToken);
        return found is null ? null : new DirectoryUser(found.UserId, found.DisplayName, found.Email ?? "");
    }

    /// <summary>Access's answer; <c>Email</c> is null from an Access that predates it.</summary>
    private sealed record UserEntry(string UserId, string DisplayName, string? Email);

    // Every part is escaped, so no value can contain the separator and make two different lookups share a key.
    private static string Key(string kind, string tenantId, string generation, params string[] parts) =>
        $"access:{kind}:{Uri.EscapeDataString(tenantId)}:{generation}:{string.Join(':', parts.Select(Uri.EscapeDataString))}";

    private FusionCacheEntryOptions EntryOptions() => new()
    {
        Duration = options.Value.CacheDuration,
        // The same in Redis: an instance that reads it from there must not treat it as fresh for longer.
        DistributedCacheDuration = options.Value.CacheDuration,
        IsFailSafeEnabled = true,
        FailSafeMaxDuration = options.Value.FailSafeMaxDuration,
        FailSafeThrottleDuration = options.Value.FailSafeThrottleDuration,
    };

    private async Task<T?> GetAsync<T>(string url, string tenantId, CancellationToken cancellationToken, bool allowNotFound = false)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ServiceTokenHandler.TenantOption, tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("The Access service returned an empty response.");
    }
}
