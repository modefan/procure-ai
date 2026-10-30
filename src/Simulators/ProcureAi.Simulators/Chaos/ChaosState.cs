using Microsoft.Extensions.Options;

namespace ProcureAi.Simulators.Chaos;

/// <summary>What one inbound call suffers: how long it waits and whether it fails with 503.</summary>
public readonly record struct ChaosDecision(TimeSpan Latency, bool Fail);

/// <summary>
/// The current chaos settings (in memory, changed through the admin endpoint) and the dice. With <c>Simulators:Random:Seed</c>
/// the sequence of latencies and failures is repeatable.
/// <para>
/// A system switched away from Normal returns to Normal by itself <see cref="ChaosRestoreOptions.AutoRestoreAfter"/> later (plan 06 ruling
/// 11): every read of the state (<see cref="Current"/>, <see cref="Decide"/>) restores a mode that is due, so nobody ever sees one that
/// overstayed, and a timer restores it on time even when nothing calls (so the log says when). Setting the same mode again keeps the
/// running countdown; another mode starts a new one. The clock is the <see cref="TimeProvider"/>, so tests control it.
/// </para>
/// </summary>
public sealed class ChaosState : IDisposable
{
    /// <summary>In <see cref="ChaosMode.Down"/> every call waits this long before it fails.</summary>
    public static readonly TimeSpan DownLatency = TimeSpan.FromMilliseconds(100);

    private const int SlowFactor = 5;

    private readonly object _gate = new();
    private readonly Random _random;
    private readonly TimeProvider _time;
    private readonly TimeSpan _restoreAfter;
    private readonly ILogger<ChaosState> _logger;
    private readonly ITimer _restoreTimer;
    private ChaosSettings _supplier;
    private ChaosSettings _erp;

    public ChaosState(IOptions<SimulatorOptions> options, IOptions<ChaosRestoreOptions> restore, TimeProvider time, ILogger<ChaosState> logger)
    {
        var value = options.Value;
        _random = value.Random.Seed is { } seed ? new Random(seed) : new Random();
        _time = time;
        _restoreAfter = restore.Value.AutoRestoreAfter;
        _logger = logger;
        _supplier = value.Chaos.Supplier.Clone();
        _erp = value.Chaos.Erp.Clone();
        // A mode configured at startup lasts as long as one switched on later.
        _supplier.RestoresAt = _supplier.Mode == ChaosMode.Normal ? null : time.GetUtcNow() + _restoreAfter;
        _erp.RestoresAt = _erp.Mode == ChaosMode.Normal ? null : time.GetUtcNow() + _restoreAfter;
        _restoreTimer = time.CreateTimer(_ => RestoreDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ArmTimer();
    }

    public ChaosConfiguration Current
    {
        get
        {
            lock (_gate)
            {
                RestoreDueLocked();
                return new ChaosConfiguration { Supplier = _supplier.Clone(), Erp = _erp.Clone() };
            }
        }
    }

    public void Set(ChaosConfiguration configuration)
    {
        lock (_gate)
        {
            RestoreDueLocked();
            _supplier = Next(_supplier, configuration.Supplier);
            _erp = Next(_erp, configuration.Erp);
            ArmTimer();
        }
    }

    public ChaosDecision Decide(ChaosTarget target)
    {
        lock (_gate)
        {
            RestoreDueLocked();
            var settings = target == ChaosTarget.Supplier ? _supplier : _erp;
            if (settings.Mode == ChaosMode.Down)
            {
                return new ChaosDecision(DownLatency, Fail: true);
            }

            var latency = settings.MinLatency + (settings.MaxLatency - settings.MinLatency) * _random.NextDouble();
            if (settings.Mode == ChaosMode.Slow)
            {
                latency *= SlowFactor;
            }

            var fail = settings.Mode == ChaosMode.Failing && _random.NextDouble() < settings.FailureRate;
            return new ChaosDecision(latency, fail);
        }
    }

    public void Dispose() => _restoreTimer.Dispose();

    private ChaosSettings Next(ChaosSettings current, ChaosSettings requested)
    {
        var next = requested.Clone();
        next.RestoresAt = next.Mode switch
        {
            ChaosMode.Normal => null,
            _ when next.Mode == current.Mode && current.RestoresAt is not null => current.RestoresAt,
            _ => _time.GetUtcNow() + _restoreAfter,
        };
        return next;
    }

    private void RestoreDue()
    {
        lock (_gate)
        {
            RestoreDueLocked();
        }
    }

    private void RestoreDueLocked()
    {
        var now = _time.GetUtcNow();
        var restored = false;
        foreach (var (target, settings) in new[] { (ChaosTarget.Supplier, _supplier), (ChaosTarget.Erp, _erp) })
        {
            if (settings.RestoresAt is { } due && due <= now)
            {
                _logger.LogInformation("Chaos mode {Mode} of the {Target} restored to Normal after {Duration}", settings.Mode, target, _restoreAfter);
                settings.Mode = ChaosMode.Normal;
                settings.RestoresAt = null;
                restored = true;
            }
        }

        if (restored)
        {
            ArmTimer();
        }
    }

    /// <summary>Points the timer at the next restore that is due (or stops it). Called under the lock.</summary>
    private void ArmTimer()
    {
        var next = new[] { _supplier.RestoresAt, _erp.RestoresAt }.Where(d => d is not null).Min();
        var wait = next is { } due ? due - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
        _restoreTimer?.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
    }
}
