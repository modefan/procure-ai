using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Chat;
using StackExchange.Redis;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// The per-conversation answer lock on Redis (DistributedLock's <see cref="RedisDistributedLock"/>, ruling 6): key
/// <c>assistant:answer:{tenant}:{conversationId}</c>, tried without waiting. Its lease (<c>Assistant:Answer:LockLease</c>, 30 s) is extended
/// in the background every third of it while the answer streams, so a long answer keeps it and a dead process loses it within one lease.
/// </summary>
internal sealed class RedisAnswerLock(IConnectionMultiplexer redis, IOptions<AssistantOptions> options, ILogger<RedisAnswerLock> logger)
    : IAnswerLock
{
    public static string KeyOf(string tenantId, Guid conversationId) => $"assistant:answer:{tenantId}:{conversationId:D}";

    public async Task<IAsyncDisposable?> TryAcquireAsync(string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var lease = options.Value.Answer.LockLease;
        var @lock = new RedisDistributedLock(
            KeyOf(tenantId, conversationId), redis.GetDatabase(), o => o.Expiry(lease).ExtensionCadence(lease / 3));
        var handle = await @lock.TryAcquireAsync(TimeSpan.Zero, cancellationToken);
        return handle is null ? null : new Held(handle, logger);
    }

    /// <summary>
    /// Releases once, however often it is disposed (the stream's finally and the end of the request both do). A release that fails (Redis
    /// unreachable) is logged, not thrown: the lease runs out by itself.
    /// </summary>
    private sealed class Held(IDistributedSynchronizationHandle handle, ILogger logger) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                await handle.DisposeAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The answer lock could not be released; its lease runs out by itself");
            }
        }
    }
}
