using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace ProcureAi.AppHost;

/// <summary>
/// Declares the durable <c>cdc</c> topic exchange through the RabbitMQ management API and reports healthy only
/// once that succeeded. Attached to the rabbitmq resource, so anything that does <c>WaitFor(rabbitmq)</c>
/// (Debezium, whose sink only publishes) is guaranteed to start after the exchange exists: Aspire documents
/// that WaitFor blocks until every health check of the dependency returns Healthy.
/// A failure keeps rabbitmq Unhealthy (visible in the dashboard with the error) and is written to the
/// rabbitmq resource log; the next health check run retries. The PUT is idempotent, so running it on every
/// check also re-creates the exchange if RabbitMQ is restarted without a volume.
/// </summary>
public sealed class CdcExchangeHealthCheck(
    EndpointReference management,
    RabbitMQServerResource rabbitmq,
    string exchange,
    ILogger resourceLogger) : IHealthCheck
{
    public const string Name = "rabbitmq-cdc-exchange";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await rabbitmq.UserNameReference.GetValueAsync(cancellationToken);
            var password = await rabbitmq.PasswordParameter.GetValueAsync(cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri($"http://{management.Host}:{management.Port}/api/exchanges/%2F/{Uri.EscapeDataString(exchange)}"))
            {
                Content = JsonContent.Create(new { type = "topic", durable = true, auto_delete = false, @internal = false }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy($"Exchange '{exchange}' declared.");
            }

            var message = $"Declaring exchange '{exchange}' failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
            resourceLogger.LogWarning("{Message}", message);
            return HealthCheckResult.Unhealthy(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Expected briefly while the management plugin starts; logged so a persistent failure is visible.
            resourceLogger.LogWarning("Declaring exchange '{Exchange}' failed: {Error}", exchange, ex.Message);
            return HealthCheckResult.Unhealthy($"Declaring exchange '{exchange}' failed.", ex);
        }
    }
}
