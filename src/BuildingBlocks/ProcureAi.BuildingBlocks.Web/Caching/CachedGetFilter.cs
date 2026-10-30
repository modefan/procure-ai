using Medallion.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using ProcureAi.BuildingBlocks.Web.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Resource filter (runs after authorization, so a forbidden caller never reaches the cache). The first
/// request takes a Redis lock and runs the action; the others wait for the lock, find the cached result and
/// replay it. Only 2xx responses are cached. A failed response is shared, for a short time, only with the
/// callers that were already waiting while it ran (on any instance); a fresh request runs again.
/// A replayed response carries the status, body, Content-Type, Location and ETag only; other headers are not kept.
/// </summary>
public sealed class CachedGetFilter(
    int seconds,
    string[] tagTemplates,
    IFusionCache cache,
    IDistributedLockProvider locks,
    ICurrentUser user,
    IOptions<CachingOptions> options,
    IOptions<FusionCacheSettings> cacheSettings,
    IDailyStats stats) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            DevPanelHeaders.Note(http, DevPanelHeaders.Cache, "bypass");
            await next();
            return;
        }

        var settings = options.Value;
        var padding = cacheSettings.Value.DistributedTtlPadding;
        var duration = TimeSpan.FromSeconds(seconds > 0 ? seconds : settings.DefaultGetSeconds);
        var key = RequestKey.ForRead(http, user);
        var tags = CacheTags.Resolve(tagTemplates, http, user);
        var failureKey = "fail:" + key;
        var lockName = cacheSettings.Value.KeyPrefix + "lock:" + key;
        var executed = default(ResponseCapture.Captured?);
        var startedUtc = DateTimeOffset.UtcNow;
        var waitedForLock = false;

        try
        {
            var response = await cache.GetOrSetAsync<StoredResponse>(
                key,
                async (factory, token) =>
                {
                    var (handle, waited) = await LockHelper.AcquireAsync(locks, lockName, settings.LockTimeout, token);
                    waitedForLock = waited;
                    if (handle is null)
                    {
                        throw new LockTimeoutException();
                    }

                    await using (handle)
                    {
                        // Another instance may have finished while we waited for the lock.
                        var existing = await cache.TryGetAsync<StoredResponse>(key, token: token);
                        if (existing.HasValue)
                        {
                            var remaining = duration - (DateTimeOffset.UtcNow - existing.Value.CreatedUtc);
                            factory.Options.Duration = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
                            factory.Options.SkipDistributedCacheWrite = true;
                            factory.Options.SkipBackplaneNotifications = true;
                            return existing.Value;
                        }

                        if (waited)
                        {
                            var failure = await cache.TryGetAsync<StoredResponse>(failureKey, CacheEntry.ReadSharedRecord, token: token);
                            if (failure.HasValue)
                            {
                                SkipAllWrites(factory.Options);
                                return failure.Value;
                            }
                        }

                        await cache.RemoveAsync(failureKey, token: token);
                        var captured = await ResponseCapture.RunAsync(context, next, requestHash: null);
                        executed = captured;
                        if (captured.Response.IsSuccess)
                        {
                            // Write the result while still holding the lock: a waiter that gets the lock next
                            // must find it. FusionCache would only write it after this factory returns.
                            await cache.SetAsync(
                                key,
                                captured.Response,
                                o => CacheEntry.Apply(o, duration, padding),
                                tags,
                                CancellationToken.None);
                            SkipAllWrites(factory.Options);
                        }
                        else
                        {
                            // Errors are never stored as results; the failure record is for waiters only.
                            SkipAllWrites(factory.Options);
                            await cache.SetAsync(
                                failureKey,
                                captured.Response,
                                o => CacheEntry.ForSharedRecord(o, settings.FailureShare, padding),
                                CancellationToken.None);
                        }

                        return captured.Response;
                    }
                },
                o => CacheEntry.Apply(o, duration, padding),
                tags,
                http.RequestAborted);

            if (executed is { } ran)
            {
                DevPanelHeaders.Note(http, DevPanelHeaders.Cache, "miss");
                if (!ran.Threw)
                {
                    await ResponseCapture.SendAsync(http, ran.Response);
                }
            }
            else
            {
                // Not run by this caller. A value created after this request began came from a call we waited for
                // (in this process, or on another instance behind the lock); an older one is a plain cache hit.
                if (waitedForLock || response.CreatedUtc >= startedUtc)
                {
                    DevPanelHeaders.Note(http, DevPanelHeaders.Cache, "miss");
                    DevPanelHeaders.Note(http, DevPanelHeaders.Coalesced, "joined");
                    await stats.TryCountCoalescedAsync();
                }
                else
                {
                    DevPanelHeaders.Note(http, DevPanelHeaders.Cache, DevPanelHeaders.CacheHit(DateTimeOffset.UtcNow - response.CreatedUtc));
                }

                context.Result = response.ToResult();
            }
        }
        catch (LockTimeoutException)
        {
            context.Result = ResponseCapture.Busy(http, settings.RetryAfterSeconds);
        }
    }

    private static void SkipAllWrites(FusionCacheEntryOptions entry)
    {
        entry.SkipMemoryCacheWrite = true;
        entry.SkipDistributedCacheWrite = true;
        entry.SkipBackplaneNotifications = true;
    }
}
