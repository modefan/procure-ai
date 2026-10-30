using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Status;
using StackExchange.Redis;

namespace ProcureAi.BuildingBlocks.Web.Status;

/// <summary>Wolverine's counts for the snapshot. The messaging building block implements it; a host without Wolverine reports none.</summary>
public interface IWolverineCounts
{
    Task<WolverineCountsDto> FetchAsync(CancellationToken cancellationToken);
}

public sealed class NoWolverineCounts : IWolverineCounts
{
    public Task<WolverineCountsDto> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(WolverineCountsDto.None);
}

/// <summary>
/// Builds this instance's <see cref="StatusSnapshot"/>: its readiness checks (the <c>ready</c> tag), its breakers, Wolverine's counts and
/// every <see cref="IStatusContributor"/>'s section. Each part gets <see cref="StatusOptions.PartTimeout"/>: a part that fails or takes
/// longer is left out (and logged), so one slow dependency (an unreachable simulator) never delays the snapshot past its TTL and turns the
/// whole service Down.
/// </summary>
public sealed class StatusSnapshotBuilder(
    ServiceIdentity identity,
    HealthCheckService health,
    ICircuitBreakerStates breakers,
    IWolverineCounts wolverine,
    IServiceScopeFactory scopes,
    IOptions<StatusOptions> options,
    TimeProvider time,
    ILogger<StatusSnapshotBuilder> logger)
{
    public async Task<StatusSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var healthReport = await PartAsync("the readiness checks",
            async token => (await health.CheckHealthAsync(r => r.Tags.Contains(Extensions.ReadyTag), token)).Status.ToString(), cancellationToken);
        var messages = await PartAsync("Wolverine's message counts", wolverine.FetchAsync, cancellationToken);

        var sections = new List<StatusSection>();
        await using var scope = scopes.CreateAsyncScope();
        foreach (var contributor in scope.ServiceProvider.GetServices<IStatusContributor>())
        {
            if (await PartAsync($"status contributor {contributor.Name}", contributor.CollectAsync, cancellationToken) is { } section)
            {
                sections.Add(section);
            }
        }

        return new StatusSnapshot(
            identity.Name, identity.InstanceId, time.GetUtcNow(), healthReport ?? nameof(HealthStatus.Unhealthy), breakers.Snapshot(),
            messages ?? WolverineCountsDto.None, sections);
    }

    /// <summary>
    /// Runs one part with its own time limit (a linked token, and a wait that gives up even if the part ignores the token). Null when it
    /// failed or timed out; the host's own stop is passed on.
    /// </summary>
    private async Task<T?> PartAsync<T>(string what, Func<CancellationToken, Task<T>> part, CancellationToken cancellationToken)
        where T : class
    {
        var limit = options.Value.PartTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            return await part(timeout.Token).WaitAsync(limit, time, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && (exception is TimeoutException || (exception is OperationCanceledException && timeout.IsCancellationRequested)))
        {
            logger.LogWarning("{Part} took longer than {Timeout}; left out of this status snapshot", what, limit);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "{Part} failed; left out of this status snapshot", what);
            return null;
        }
    }
}

/// <summary>
/// Writes this instance's snapshot to Redis every <see cref="StatusOptions.PublishInterval"/> (key <see cref="StatusKeys.Snapshot"/>, TTL
/// <see cref="StatusOptions.Ttl"/>) and adds the key to <see cref="StatusKeys.Index"/>, scored by its expiry. Never throws into the host:
/// a failure (Redis down) is logged and the next interval tries again.
/// </summary>
public sealed class StatusPublisher(
    StatusSnapshotBuilder builder,
    IConnectionMultiplexer redis,
    IOptions<StatusOptions> options,
    TimeProvider time,
    ILogger<StatusPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PublishInterval;
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            await PublishOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Builds and writes one snapshot; false (logged) when it could not be written.</summary>
    public async Task<bool> PublishOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await builder.BuildAsync(cancellationToken);
            var ttl = options.Value.Ttl;
            var key = StatusKeys.Snapshot(snapshot.Service, snapshot.InstanceId);
            var now = time.GetUtcNow();
            var db = redis.GetDatabase();
            var json = JsonSerializer.Serialize(snapshot, StatusKeys.Json);
            var batch = db.CreateBatch();
            var write = batch.StringSetAsync(key, json, ttl);
            var index = batch.SortedSetAddAsync(StatusKeys.Index, key, (now + ttl).ToUnixTimeMilliseconds());
            // Members whose snapshot has expired (an instance that stopped) are dropped by every writer.
            var prune = batch.SortedSetRemoveRangeByScoreAsync(StatusKeys.Index, double.NegativeInfinity, now.ToUnixTimeMilliseconds());
            batch.Execute();
            await Task.WhenAll(write, index, prune);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not write the status snapshot; trying again at the next interval");
            return false;
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
