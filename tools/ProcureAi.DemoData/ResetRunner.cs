using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProcureAi.DemoData;

/// <summary>The two HTTP clients of a reset: the gateway (every service) and the simulators (not behind the gateway).</summary>
public sealed record ResetClients(HttpClient Gateway, HttpClient Simulators);

/// <summary>
/// <c>reset</c> without the seed (plan 06 ruling 4): the simulators first, then every service's <c>internal/demo/reset</c> through the
/// gateway in parallel, then, after <see cref="DemoDataOptions.AuditGrace"/> (late change data capture lands first), Audit last. Every call
/// carries the admin key; a failed call is retried <see cref="DemoDataOptions.ResetRetries"/> times. A call that still fails stops the reset
/// (after the parallel phase finished, and before Audit, so the audit of what happened stays) and the result is false: the next run starts
/// from the beginning, which is safe because every service's reset is idempotent.
/// <para>
/// No resilience handler of its own: a reset may take longer than the standard handler's attempt timeout, and a refused call (401, 403,
/// 404 when <c>DemoReset:Enabled</c> is off) must not be retried.
/// </para>
/// </summary>
public sealed class ResetRunner(ResetClients clients, IOptions<DemoDataOptions> options, TimeProvider time, ILogger<ResetRunner> logger)
{
    /// <summary>The services reset in parallel after the simulators (ruling 4's list, plus Notifications' bell).</summary>
    public static readonly IReadOnlyList<string> ParallelServices =
        ["requests", "approvals", "budgets", "purchasing", "invoicing", "assistant", "suppliers", "access", "notifications"];

    public const string AuditService = "audit";

    public const string SimulatorsPath = "api/v1/simulators/reset";

    public static string ServicePath(string service) => $"api/v1/{service}/internal/demo/reset";

    /// <summary>Every phase in order. True when every call succeeded.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken) =>
        await ResetServicesAsync(cancellationToken) && await ResetAuditAsync(cancellationToken);

    /// <summary>The simulators, then the services in parallel (all of them run, even when one fails).</summary>
    public async Task<bool> ResetServicesAsync(CancellationToken cancellationToken)
    {
        if (!await CallAsync("simulators", clients.Simulators, SimulatorsPath, cancellationToken))
        {
            return false;
        }

        var results = await Task.WhenAll(ParallelServices.Select(s => CallAsync(s, clients.Gateway, ServicePath(s), cancellationToken)));
        return results.All(ok => ok);
    }

    /// <summary>After the grace period, Audit.</summary>
    public async Task<bool> ResetAuditAsync(CancellationToken cancellationToken)
    {
        var grace = options.Value.AuditGrace;
        if (grace > TimeSpan.Zero)
        {
            logger.LogInformation("Waiting {Seconds} s for late change data capture before the audit is cleared", grace.TotalSeconds);
            await Task.Delay(grace, time, cancellationToken);
        }

        return await CallAsync(AuditService, clients.Gateway, ServicePath(AuditService), cancellationToken);
    }

    private async Task<bool> CallAsync(string name, HttpClient http, string path, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            string outcome;
            var retry = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.ResetCallTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Post, path);
                message.Headers.Add("X-Api-Key", settings.AdminApiKey);
                message.Headers.Add("X-Calling-Service", GatewayClient.CallingService);
                using var response = await http.SendAsync(message, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Reset {Service} in {Milliseconds} ms", name, clock.ElapsedMilliseconds);
                    return true;
                }

                outcome = $"{(int)response.StatusCode} {Truncate(await response.Content.ReadAsStringAsync(CancellationToken.None))}";
                retry = response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                outcome = e is OperationCanceledException ? $"no answer within {settings.ResetCallTimeout}" : e.Message;
            }

            if (!retry || attempt > settings.ResetRetries)
            {
                logger.LogError("Reset of {Service} failed after {Attempts} attempt(s): {Outcome}", name, attempt, outcome);
                return false;
            }

            logger.LogWarning("Reset of {Service} failed (attempt {Attempt}): {Outcome}; retrying", name, attempt, outcome);
            if (settings.ResetRetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(settings.ResetRetryDelay, time, cancellationToken);
            }
        }
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300] + "...";
}
