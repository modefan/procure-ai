using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProcureAi.DemoData;

/// <summary>The two HTTP clients of a reset: the gateway (every service) and the simulators (not behind the gateway).</summary>
public sealed record ResetClients(HttpClient Gateway, HttpClient Simulators);

/// <summary>
/// <c>reset</c> without the seed (plan 06 ruling 4): a preflight that every target has its reset switched on (<see cref="PreflightAsync"/>),
/// then the gateway's maintenance window (<see cref="MaintenancePath"/>, plan 07 Task 0b: users' writes get 503 until it is lifted again at
/// the end, also after a failure; a run that cannot open it resets nothing), then the simulators first, then every service's
/// <c>internal/demo/reset</c> through the
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

    /// <summary>The gateway's maintenance flag: <c>PUT</c> sets it (with its TTL), <c>DELETE</c> lifts it. Admin key only.</summary>
    public const string MaintenancePath = "api/v1/gateway/maintenance";

    public static string ServicePath(string service) => $"api/v1/{service}/internal/demo/reset";

    /// <summary>
    /// Every phase in order, after the preflight, inside the maintenance window. True when every call succeeded. The window is lifted in a
    /// <c>finally</c>; a process that is killed leaves it to expire by its TTL (the gateway's <c>DemoReset:MaintenanceTtl</c>, 10 min).
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (!await PreflightAsync(cancellationToken))
        {
            return false;
        }

        if (!await CallAsync(MaintenanceWindow, clients.Gateway, MaintenancePath, HttpMethod.Put, cancellationToken))
        {
            logger.LogError("The gateway's maintenance window could not be opened; nothing was reset");
            return false;
        }

        try
        {
            return await ResetServicesAsync(cancellationToken) && await ResetAuditAsync(cancellationToken);
        }
        finally
        {
            // Also when the run is being stopped: the users must not wait for the TTL.
            if (!await CallAsync(MaintenanceWindow, clients.Gateway, MaintenancePath, HttpMethod.Delete, CancellationToken.None))
            {
                logger.LogError("The gateway's maintenance window could not be lifted; it expires by itself (DemoReset:MaintenanceTtl)");
            }
        }
    }

    private const string MaintenanceWindow = "maintenance window";

    /// <summary>
    /// Before anything is reset: one <c>GET</c> on every target's reset route (the simulators, every service, Audit), in parallel. The routes
    /// take only POST, so a target with <c>DemoReset:Enabled</c> on answers 405 and runs nothing; one with it off has no route and answers
    /// 404. A 404, 401 or 403 (the reset call would be refused the same way, without a retry) stops the reset here, before the simulators
    /// forget their orders and restart their numbering. Anything else (no answer, 5xx) is left to the reset call, which retries.
    /// </summary>
    public async Task<bool> PreflightAsync(CancellationToken cancellationToken)
    {
        var refused = (await Task.WhenAll(Targets().Select(async t => (t.Name, Status: await ProbeAsync(t.Http, t.Path, cancellationToken)))))
            .Where(p => p.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            .ToList();
        foreach (var (name, status) in refused)
        {
            logger.LogError(
                "Reset of {Service} would be refused ({Status} {Reason}): DemoReset:Enabled is off there, or the key is wrong. Nothing was reset",
                name, (int)status!, status);
        }

        return refused.Count == 0;
    }

    /// <summary>The simulators, then the services in parallel (all of them run, even when one fails).</summary>
    public async Task<bool> ResetServicesAsync(CancellationToken cancellationToken)
    {
        if (!await CallAsync("simulators", clients.Simulators, SimulatorsPath, HttpMethod.Post, cancellationToken))
        {
            return false;
        }

        var results = await Task.WhenAll(ParallelServices.Select(s => CallAsync(s, clients.Gateway, ServicePath(s), HttpMethod.Post, cancellationToken)));
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

        return await CallAsync(AuditService, clients.Gateway, ServicePath(AuditService), HttpMethod.Post, cancellationToken);
    }

    private IEnumerable<(string Name, HttpClient Http, string Path)> Targets() =>
    [
        ("simulators", clients.Simulators, SimulatorsPath),
        .. ParallelServices.Select(s => (s, clients.Gateway, ServicePath(s))),
        (AuditService, clients.Gateway, ServicePath(AuditService)),
    ];

    /// <summary>The probe's status code, or null when there was no answer in time (the reset call deals with that).</summary>
    private async Task<HttpStatusCode?> ProbeAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.ResetCallTimeout);
            using var message = Authorized(new HttpRequestMessage(HttpMethod.Get, path));
            using var response = await http.SendAsync(message, timeout.Token);
            return response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private HttpRequestMessage Authorized(HttpRequestMessage message)
    {
        message.Headers.Add("X-Api-Key", options.Value.AdminApiKey);
        message.Headers.Add("X-Calling-Service", GatewayClient.CallingService);
        return message;
    }

    /// <summary>One call (a reset: POST; the maintenance window: PUT or DELETE), retried <see cref="DemoDataOptions.ResetRetries"/> times.</summary>
    private async Task<bool> CallAsync(string name, HttpClient http, string path, HttpMethod method, CancellationToken cancellationToken)
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
                using var message = Authorized(new HttpRequestMessage(method, path));
                using var response = await http.SendAsync(message, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("{Action} {Service} done in {Milliseconds} ms", Verb(method), name, clock.ElapsedMilliseconds);
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
                logger.LogError("{Action} {Service} failed after {Attempts} attempt(s): {Outcome}", Verb(method), name, attempt, outcome);
                return false;
            }

            logger.LogWarning("{Action} {Service} failed (attempt {Attempt}): {Outcome}; retrying", Verb(method), name, attempt, outcome);
            if (settings.ResetRetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(settings.ResetRetryDelay, time, cancellationToken);
            }
        }
    }

    private static string Verb(HttpMethod method) =>
        method == HttpMethod.Put ? "Opening the" : method == HttpMethod.Delete ? "Lifting the" : "Reset of";

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300] + "...";
}
