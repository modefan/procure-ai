using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Retention;
using Wolverine;

namespace ProcureAi.Audit.Infrastructure.Retention;

/// <summary>
/// Sends <see cref="PurgeExpiredDataChanges"/> every <c>Audit:PurgeInterval</c> (daily) of the <see cref="TimeProvider"/>, the first
/// time one interval after startup. Every instance runs it; the purge is idempotent, so a second run only finds nothing. A failed
/// run is logged and tried again at the next interval.
/// </summary>
public sealed class DataChangeRetention(
    IServiceScopeFactory scopes, IOptions<AuditOptions> options, TimeProvider clock, ILogger<DataChangeRetention> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PurgeInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new PurgeExpiredDataChanges(), stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "The data change purge failed; it runs again in {Interval}", options.Value.PurgeInterval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
