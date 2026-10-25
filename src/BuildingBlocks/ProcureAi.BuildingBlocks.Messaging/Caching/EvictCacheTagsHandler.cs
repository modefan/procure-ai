using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Messaging.Caching;

public static class EvictCacheTagsHandler
{
    /// <summary>
    /// Evicts within <see cref="CachingOptions.InvalidationTimeout"/>. The write that asked for it has already committed, so a
    /// failure or a timeout is logged and the message counts as handled (no retries): the cached entries expire on their own.
    /// </summary>
    public static async Task Handle(
        EvictCacheTags message, ICacheInvalidator cache, IOptions<CachingOptions> options, ILogger<EvictCacheTags> logger)
    {
        using var timeout = new CancellationTokenSource(options.Value.InvalidationTimeout);
        try
        {
            await cache.RemoveByTagAsync(message.Tags, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Cache eviction of {Tags} failed or timed out after {Timeout}; the cached reads expire on their own",
                message.Tags, options.Value.InvalidationTimeout);
        }
    }
}
