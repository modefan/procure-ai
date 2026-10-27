using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ProcureAi.Simulators.Supplier;

namespace ProcureAi.Simulators.Webhooks;

/// <summary>
/// Delivers the supplier's webhooks: waits until each is due, then POSTs it signed to the receiver. A failed delivery (an error
/// status, a timeout, no connection) is retried after each of <c>Webhooks:RetryDelays</c>; after the last the webhook is dropped and
/// logged, because from then on it is the receiver's fault. A cancelled order's webhooks are dropped before each attempt.
/// </summary>
internal sealed class WebhookSender(
    WebhookQueue queue,
    SupplierStore store,
    IHttpClientFactory httpClients,
    IOptions<SimulatorOptions> options,
    TimeProvider time,
    ILogger<WebhookSender> logger) : BackgroundService
{
    public const string ClientName = "simulator-webhooks";

    private readonly ConcurrentDictionary<Task, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var webhook in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var task = Task.Run(() => DeliverAsync(webhook, stoppingToken), CancellationToken.None);
                _running.TryAdd(task, 0);
                _ = task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the deliveries below see the same token.
        }

        await Task.WhenAll(_running.Keys);
    }

    private async Task DeliverAsync(ScheduledWebhook webhook, CancellationToken cancellationToken)
    {
        try
        {
            var webhooks = options.Value.Webhooks;
            var wait = webhook.DueAt - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            for (var attempt = 0; ; attempt++)
            {
                if (store.IsCancelled(webhook.SupplierOrderId))
                {
                    logger.LogInformation("Dropped {Type} of cancelled order {SupplierOrderId}", webhook.Type, webhook.SupplierOrderId);
                    return;
                }

                if (await TrySendAsync(webhook, attempt + 1, cancellationToken))
                {
                    return;
                }

                if (attempt >= webhooks.RetryDelays.Length)
                {
                    logger.LogWarning(
                        "Gave up delivering {Type} of order {SupplierOrderId} to {Target} after {Attempts} attempts (the receiver's fault); webhook dropped",
                        webhook.Type, webhook.SupplierOrderId, webhook.Target, attempt + 1);
                    return;
                }

                await Task.Delay(webhooks.RetryDelays[attempt], time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The simulator is stopping.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Delivering {Type} of order {SupplierOrderId} failed unexpectedly; webhook dropped", webhook.Type, webhook.SupplierOrderId);
        }
    }

    private async Task<bool> TrySendAsync(ScheduledWebhook webhook, int attempt, CancellationToken cancellationToken)
    {
        var webhooks = options.Value.Webhooks;
        var url = webhook.Target == WebhookTarget.Purchasing ? webhooks.PurchasingUrl : webhooks.InvoicingUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Not configured (or not a URL): retrying cannot help, so this ends the delivery like a success would.
            logger.LogError(
                "No receiver URL for {Target} (Simulators:Webhooks:{Setting}): {Type} of order {SupplierOrderId} dropped",
                webhook.Target, webhook.Target == WebhookTarget.Purchasing ? "PurchasingUrl" : "InvoicingUrl", webhook.Type, webhook.SupplierOrderId);
            return true;
        }

        if (string.IsNullOrEmpty(webhooks.Secret))
        {
            logger.LogError("No webhook secret (Simulators:Webhooks:Secret): {Type} of order {SupplierOrderId} dropped, never sent unsigned", webhook.Type, webhook.SupplierOrderId);
            return true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(webhook.Body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // A fresh timestamp for every attempt, so a retry 30 s later is not "too old" for the receiver; the body is the same bytes.
            request.Headers.Add(WebhookSigner.HeaderName, WebhookSigner.CreateHeader(webhooks.Secret, time.GetUtcNow().ToUnixTimeSeconds(), webhook.Body));

            using var client = httpClients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Delivered {Type} of order {SupplierOrderId} to {Target} (attempt {Attempt})", webhook.Type, webhook.SupplierOrderId, webhook.Target, attempt);
                return true;
            }

            logger.LogWarning("Delivering {Type} of order {SupplierOrderId} to {Target} got {Status} (attempt {Attempt})", webhook.Type, webhook.SupplierOrderId, webhook.Target, (int)response.StatusCode, attempt);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Delivering {Type} of order {SupplierOrderId} to {Target} failed (attempt {Attempt})", webhook.Type, webhook.SupplierOrderId, webhook.Target, attempt);
        }

        return false;
    }
}
