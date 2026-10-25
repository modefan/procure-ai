using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// The generation of a tenant's cached Access answers, part of every answer's cache key. Advancing it (when Access announces a
/// change) makes every older entry unreachable, which tag eviction cannot do: that only expires entries softly, and fail-safe
/// would still serve them as "last good" while Access is down, granting what was just revoked. The generation lives in the
/// shared cache (Redis and backplane), so every instance sees the new one. A tenant without one is on <see cref="Initial"/>.
/// </summary>
public sealed class AccessCacheGeneration(IFusionCache cache, IOptions<AccessClientOptions> options)
{
    public const string Initial = "0";

    // Must outlive the longest an old entry can live (fail-safe), or a lapsed generation would make old entries reachable again.
    private FusionCacheEntryOptions EntryOptions() => new()
    {
        Duration = options.Value.FailSafeMaxDuration * 2,
        DistributedCacheDuration = options.Value.FailSafeMaxDuration * 2,
        IsFailSafeEnabled = false,
    };

    public async ValueTask<string> GetAsync(string tenantId, CancellationToken cancellationToken) =>
        await cache.GetOrDefaultAsync<string>(Key(tenantId), Initial, EntryOptions(), cancellationToken) ?? Initial;

    public async ValueTask AdvanceAsync(string tenantId, CancellationToken cancellationToken = default) =>
        await cache.SetAsync(Key(tenantId), Guid.NewGuid().ToString("N"), EntryOptions(), token: cancellationToken);

    private static string Key(string tenantId) => $"access:gen:{Uri.EscapeDataString(tenantId)}";
}
