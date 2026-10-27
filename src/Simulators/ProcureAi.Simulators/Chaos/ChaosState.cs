using Microsoft.Extensions.Options;

namespace ProcureAi.Simulators.Chaos;

/// <summary>What one inbound call suffers: how long it waits and whether it fails with 503.</summary>
public readonly record struct ChaosDecision(TimeSpan Latency, bool Fail);

/// <summary>
/// The current chaos settings (in memory, changed through the admin endpoint) and the dice. With <c>Simulators:Random:Seed</c>
/// the sequence of latencies and failures is repeatable.
/// </summary>
public sealed class ChaosState
{
    /// <summary>In <see cref="ChaosMode.Down"/> every call waits this long before it fails.</summary>
    public static readonly TimeSpan DownLatency = TimeSpan.FromMilliseconds(100);

    private const int SlowFactor = 5;

    private readonly object _gate = new();
    private readonly Random _random;
    private ChaosSettings _supplier;
    private ChaosSettings _erp;

    public ChaosState(IOptions<SimulatorOptions> options)
    {
        var value = options.Value;
        _random = value.Random.Seed is { } seed ? new Random(seed) : new Random();
        _supplier = value.Chaos.Supplier.Clone();
        _erp = value.Chaos.Erp.Clone();
    }

    public ChaosConfiguration Current
    {
        get
        {
            lock (_gate)
            {
                return new ChaosConfiguration { Supplier = _supplier.Clone(), Erp = _erp.Clone() };
            }
        }
    }

    public void Set(ChaosConfiguration configuration)
    {
        lock (_gate)
        {
            _supplier = configuration.Supplier.Clone();
            _erp = configuration.Erp.Clone();
        }
    }

    public ChaosDecision Decide(ChaosTarget target)
    {
        lock (_gate)
        {
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
}
