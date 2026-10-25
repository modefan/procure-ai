using Medallion.Threading;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

internal static class CacheEntry
{
    /// <summary>Memory and Redis duration. The Redis TTL is padded, see <see cref="FusionCacheSettings.DistributedTtlPadding"/>.</summary>
    public static void Apply(FusionCacheEntryOptions options, TimeSpan duration, TimeSpan padding)
    {
        options.Duration = duration;
        options.DistributedCacheDuration = duration + padding;
    }

    /// <summary>
    /// For records read by other instances only (idempotent results, shared failures): kept in Redis, never in
    /// process memory, and no backplane message because there is no memory copy to invalidate.
    /// </summary>
    public static void ForSharedRecord(FusionCacheEntryOptions options, TimeSpan duration, TimeSpan padding)
    {
        Apply(options, duration, padding);
        options.SkipMemoryCacheRead = true;
        options.SkipMemoryCacheWrite = true;
        options.SkipBackplaneNotifications = true;
    }

    public static void ReadSharedRecord(FusionCacheEntryOptions options) => options.SkipMemoryCacheRead = true;
}

internal static class LockHelper
{
    /// <summary>
    /// Tries the lock without waiting first; if it is held, waits up to <paramref name="timeout"/>.
    /// <c>Waited</c> tells the caller another call was running, so it may share that call's failure.
    /// A null handle means the wait timed out.
    /// </summary>
    public static async Task<(IDistributedSynchronizationHandle? Handle, bool Waited)> AcquireAsync(
        IDistributedLockProvider locks, string name, TimeSpan timeout, CancellationToken token)
    {
        var handle = await locks.TryAcquireLockAsync(name, TimeSpan.Zero, token);
        if (handle is not null)
        {
            return (handle, false);
        }

        return (await locks.TryAcquireLockAsync(name, timeout, token), true);
    }
}
