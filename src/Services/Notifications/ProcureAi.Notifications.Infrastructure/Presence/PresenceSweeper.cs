using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Notifications.Application.Presence;

namespace ProcureAi.Notifications.Infrastructure.Presence;

/// <summary>
/// Every <see cref="PresenceOptions.SweepInterval"/> (on the presence clock) drops the connections without a heartbeat for longer than the
/// TTL, of every instance (the state is in Redis), and sends the changed online and viewer lists through the backplane. Every instance
/// runs it; a DistributedLock (Redis) lets one sweep at a time, the others wait up to <see cref="PresenceOptions.SweepLockWait"/> and then
/// sweep what is left (usually nothing).
/// </summary>
public sealed class PresenceSweeper(
    PresenceService presence,
    IDistributedLockProvider locks,
    IOptions<PresenceOptions> options,
    [FromKeyedServices(PresenceClock.Key)] TimeProvider clock,
    ILogger<PresenceSweeper> logger) : BackgroundService
{
    public const string LockName = "notifications:presence-sweep";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Redis away for a moment: the next tick tries again. Readers already ignore stale entries.
                    logger.LogWarning(exception, "Presence sweep failed; trying again in {Interval}", options.Value.SweepInterval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One sweep under the lock. Null when another instance held the lock for longer than the wait.</summary>
    public async Task<int?> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var handle = await locks.TryAcquireLockAsync(LockName, options.Value.SweepLockWait, cancellationToken);
        if (handle is null)
        {
            return null;
        }

        return await presence.SweepAsync(cancellationToken);
    }
}
