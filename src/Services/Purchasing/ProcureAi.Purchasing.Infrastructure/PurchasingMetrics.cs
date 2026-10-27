using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Infrastructure;

/// <summary>
/// The Purchasing service's business metrics on the OpenTelemetry meter <c>ProcureAi.Purchasing</c> (exported with every
/// <c>ProcureAi.*</c> meter by the service defaults): failed sends to the supplier by cause, and how many orders are waiting to
/// reach the supplier (all tenants, counted by <see cref="WaitingOrdersCounter"/>). The circuit breaker's own events are Polly's
/// <c>resilience.polly.*</c> metrics (meter <c>Polly</c>, also exported).
/// </summary>
public sealed class PurchasingMetrics : IPurchasingMetrics
{
    public const string MeterName = "ProcureAi.Purchasing";
    public const string SupplierSendFailures = "procure.purchasing.supplier_send_failures";
    public const string OrdersWaitingForSupplier = "procure.purchasing.orders_waiting_for_supplier";

    private readonly Counter<long> _sendFailures;
    private long _waiting;

    public PurchasingMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _sendFailures = meter.CreateCounter<long>(
            SupplierSendFailures, unit: "{attempt}", description: "Attempts to send an order to the supplier that failed, by reason.");
        meter.CreateObservableGauge(
            OrdersWaitingForSupplier, () => Interlocked.Read(ref _waiting), unit: "{order}",
            description: "Orders not yet accepted by the supplier (waiting or retrying).");
    }

    public void SupplierSendFailed(string reason) =>
        _sendFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>The last count of waiting orders (the gauge's value).</summary>
    public long Waiting => Interlocked.Read(ref _waiting);

    internal void SetWaiting(long count) => Interlocked.Exchange(ref _waiting, count);
}

/// <summary>
/// Counts the orders waiting for the supplier every <c>Purchasing:WaitingOrdersRefresh</c>, so the gauge never queries the database
/// on the exporter's thread. A failed count (database down) is logged and the gauge keeps its last value.
/// </summary>
internal sealed class WaitingOrdersCounter(
    IServiceScopeFactory scopes, PurchasingMetrics metrics, IOptions<PurchasingOptions> options, ILogger<WaitingOrdersCounter> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first count comes after one interval, not at startup: a host that only starts briefly (the build step that writes the
        // OpenAPI document, with no database) never queries.
        using var timer = new PeriodicTimer(options.Value.WaitingOrdersRefresh);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
                metrics.SetWaiting(await db.Orders.IgnoreQueryFilters().LongCountAsync(o => o.Status == OrderStatus.PendingSupplier, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Counting the orders waiting for the supplier failed; the gauge keeps its last value");
            }
        }
    }
}
