using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using Wolverine;

namespace ProcureAi.Assistant.Infrastructure.Retrieval;

/// <summary>
/// Sends <see cref="PurgeStalePlaceholders"/> every <c>Assistant:Purchases:PlaceholderCleanupInterval</c> of the
/// <see cref="TimeProvider"/>, the first time one interval after startup. Every instance runs it; the delete is idempotent. A failed run
/// is logged and tried again at the next interval.
/// </summary>
public sealed class PlaceholderCleanup(
    IServiceScopeFactory scopes, IOptions<AssistantOptions> options, TimeProvider clock, ILogger<PlaceholderCleanup> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Purchases.PlaceholderCleanupInterval;
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new PurgeStalePlaceholders(), stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "The placeholder purchase cleanup failed; it runs again in {Interval}", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
