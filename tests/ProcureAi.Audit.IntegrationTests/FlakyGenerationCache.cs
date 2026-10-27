using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// Wraps the service's real Redis cache and fails the next write of the Access cache generation of chosen tenants once, as a
/// Redis blip would. Everything else passes through, so the other tests of the collection are not affected.
/// </summary>
public sealed class FlakyGenerationCache
{
    private readonly ConcurrentDictionary<string, int> _pending = new();
    private readonly ConcurrentDictionary<string, int> _written = new();
    private int _failed;

    public int Failed => _failed;

    /// <summary>True once a write of <paramref name="tenant"/>'s generation has reached Redis.</summary>
    public bool Written(string tenant) => _written.Keys.Any(k => k.EndsWith($"access:gen:{tenant}", StringComparison.Ordinal));

    /// <summary>The next write of <paramref name="tenant"/>'s generation fails.</summary>
    public void FailNextWriteOf(string tenant) => _pending[$"access:gen:{tenant}"] = 1;

    /// <summary>Replaces the registered <see cref="IDistributedCache"/> with a wrapper around it.</summary>
    public void Install(IServiceCollection services)
    {
        var original = services.Last(d => d.ServiceType == typeof(IDistributedCache));
        services.Remove(original);
        services.AddSingleton<IDistributedCache>(sp => new Wrapper(this, Resolve(sp, original)));
    }

    private static IDistributedCache Resolve(IServiceProvider services, ServiceDescriptor original) => original switch
    {
        { ImplementationInstance: IDistributedCache instance } => instance,
        { ImplementationFactory: { } factory } => (IDistributedCache)factory(services),
        { ImplementationType: { } type } => (IDistributedCache)ActivatorUtilities.CreateInstance(services, type),
        _ => throw new InvalidOperationException("Unsupported IDistributedCache registration."),
    };

    private void FailIfChosen(string key)
    {
        // FusionCache prefixes keys (service prefix, wire format), so match the end.
        foreach (var chosen in _pending.Keys)
        {
            if (key.EndsWith(chosen, StringComparison.Ordinal) && _pending.TryRemove(chosen, out _))
            {
                Interlocked.Increment(ref _failed);
                throw new InvalidOperationException("Redis is unreachable (test).");
            }
        }
    }

    private sealed class Wrapper(FlakyGenerationCache faults, IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            faults.FailIfChosen(key);
            inner.Set(key, value, options);
            faults._written[key] = 1;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            faults.FailIfChosen(key);
            faults._written[key] = 1;
            return inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
