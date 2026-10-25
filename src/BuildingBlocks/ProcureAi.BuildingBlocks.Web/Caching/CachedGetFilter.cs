using Medallion.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Resource filter (runs after authorization, so a forbidden caller never reaches the cache). The first
/// request takes a Redis lock and runs the action; the others wait for the lock, find the cached result and
/// replay it. Only 2xx responses are cached.
/// </summary>
public sealed class CachedGetFilter(
    int seconds,
    IFusionCache cache,
    IDistributedLockProvider locks,
    ICurrentUser user,
    IOptions<CachingOptions> options) : IAsyncResourceFilter
{
    private static readonly TimeSpan DistributedGrace = TimeSpan.FromSeconds(1);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            await next();
            return;
        }

        var settings = options.Value;
        var duration = TimeSpan.FromSeconds(seconds > 0 ? seconds : settings.DefaultGetSeconds);
        var key = RequestKey.ForRead(http, user);
        var executedHere = false;

        try
        {
            var response = await cache.GetOrSetAsync<StoredResponse>(
                key,
                async (factory, token) =>
                {
                    await using var handle = await locks.TryAcquireLockAsync("lock:" + key, settings.LockTimeout, token)
                        ?? throw new TimeoutException("Timed out waiting for the same request running elsewhere.");

                    // Another instance may have finished while we waited for the lock.
                    var existing = await cache.TryGetAsync<StoredResponse>(key, token: token);
                    if (existing.HasValue)
                    {
                        var remaining = duration - (DateTimeOffset.UtcNow - existing.Value.CreatedUtc);
                        factory.Options.Duration = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
                        return existing.Value;
                    }

                    executedHere = true;
                    var captured = await ResponseCapture.RunAsync(context, next, requestHash: null);
                    if (!captured.IsSuccess)
                    {
                        // Errors are shared with the waiters on this instance, but never stored.
                        factory.Options.SkipMemoryCacheWrite = true;
                        factory.Options.SkipDistributedCacheWrite = true;
                        factory.Options.SkipBackplaneNotifications = true;
                    }

                    return captured;
                },
                options =>
                {
                    options.SetDuration(duration);
                    // FusionCache did not write 1 s entries to Redis at all (found by the cross-instance test).
                    // The entry carries its own logical expiry, so a longer Redis TTL never serves stale data.
                    options.DistributedCacheDuration = duration + DistributedGrace;
                },
                http.RequestAborted);

            if (!executedHere)
            {
                context.Result = response.ToResult();
            }
        }
        catch (TimeoutException)
        {
            context.Result = ResponseCapture.Busy(http, settings.RetryAfterSeconds);
        }
    }
}
