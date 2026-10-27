using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// The generation of a tenant's cached Access answers, part of every answer's cache key. Advancing it (when Access announces a
/// change) makes every older entry unreachable, which tag eviction cannot do: that only expires entries softly, and fail-safe
/// would still serve them as "last good" while Access is down, granting what was just revoked. The generation lives in the
/// shared cache (Redis and backplane), so every instance sees the new one. A tenant without one is on <see cref="Initial"/>.
/// <para>
/// Only one instance of a service handles <c>AccessChanged</c> (competing consumers); the others learn of it through Redis.
/// So <see cref="AdvanceAsync"/> writes Redis and the backplane in the foreground and throws when either fails: the message is
/// then retried instead of acked. And an instance trusts its in-memory copy only for
/// <see cref="AccessClientOptions.GenerationMemoryCacheDuration"/>, so one that missed the backplane message reads the new
/// generation from Redis within seconds. While Redis cannot be read it keeps the last generation it knew (fail-safe), never
/// falling back to <see cref="Initial"/>, which would make answers cached before any change reachable again.
/// </para>
/// </summary>
public sealed class AccessCacheGeneration(IFusionCache cache, IOptions<AccessClientOptions> options)
{
    public const string Initial = "0";

    // Must outlive the longest an old entry can live (fail-safe), or a lapsed generation would make old entries reachable again.
    private FusionCacheEntryOptions ReadOptions()
    {
        var settings = options.Value;
        var lifetime = settings.FailSafeMaxDuration * 2;
        return new()
        {
            Duration = lifetime,
            MemoryCacheDuration = settings.GenerationMemoryCacheDuration,
            DistributedCacheDuration = lifetime,
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = lifetime,
            DistributedCacheFailSafeMaxDuration = lifetime,
            AllowStaleOnReadOnly = true,
        };
    }

    private FusionCacheEntryOptions WriteOptions()
    {
        var write = ReadOptions();
        write.ReThrowDistributedCacheExceptions = true;
        write.ReThrowBackplaneExceptions = true;
        write.AllowBackgroundDistributedCacheOperations = false;
        write.AllowBackgroundBackplaneOperations = false;
        return write;
    }

    public async ValueTask<string> GetAsync(string tenantId, CancellationToken cancellationToken) =>
        await cache.GetOrDefaultAsync<string>(Key(tenantId), Initial, ReadOptions(), cancellationToken) ?? Initial;

    /// <summary>Moves the tenant to a new generation on every instance. Throws when Redis or the backplane could not be told.</summary>
    public async ValueTask AdvanceAsync(string tenantId, CancellationToken cancellationToken = default) =>
        await cache.SetAsync(Key(tenantId), Guid.NewGuid().ToString("N"), WriteOptions(), token: cancellationToken);

    private static string Key(string tenantId) => $"access:gen:{Uri.EscapeDataString(tenantId)}";
}
