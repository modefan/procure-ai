using System.Collections.Concurrent;
using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.BuildingBlocks.Web.Status;

/// <summary>A breaker this process has, so it is listed (Closed) before its first state change. Registered by the code that adds the pipeline.</summary>
public sealed record KnownCircuitBreaker(string Name);

/// <summary>
/// The breakers of this process, in memory: each Polly circuit breaker reports its changes from its <c>OnOpened</c> / <c>OnClosed</c> /
/// <c>OnHalfOpened</c> callbacks. A singleton; the snapshot lists them by name.
/// </summary>
public sealed class CircuitBreakerStates : ICircuitBreakerStates
{
    private readonly ConcurrentDictionary<string, BreakerDto> _breakers = new(StringComparer.Ordinal);

    public CircuitBreakerStates(IEnumerable<KnownCircuitBreaker> known)
    {
        foreach (var breaker in known)
        {
            _breakers.TryAdd(breaker.Name, new BreakerDto(breaker.Name, CircuitState.Closed, null));
        }
    }

    public void Report(string name, CircuitState state, DateTimeOffset? retryAt) =>
        _breakers[name] = new BreakerDto(name, state, state == CircuitState.Open ? retryAt : null);

    public IReadOnlyList<BreakerDto> Snapshot() => [.. _breakers.Values.OrderBy(b => b.Name, StringComparer.Ordinal)];
}
