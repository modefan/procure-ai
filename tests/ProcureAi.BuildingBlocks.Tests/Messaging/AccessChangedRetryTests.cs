using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.Contracts.Access;
using Shouldly;
using Wolverine;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

/// <summary>
/// Fix B round 2: when Redis fails while <c>AccessChanged</c> is handled, the message is retried (scheduled) until the new
/// generation is stored, instead of going to the error queue on the first attempt (Wolverine's default for an exception no
/// rule matches).
/// </summary>
public sealed class AccessChangedRetryTests
{
    private static readonly TimeSpan[] FastDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(IHost Host, FlakyDistributedCache Redis, AccessCacheGeneration Generation)> StartAsync(int failures)
    {
        var redis = new FlakyDistributedCache(failures);
        var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
                cache.SetupDistributedCache(redis, new FusionCacheSystemTextJsonSerializer());
                services.AddSingleton<IFusionCache>(cache);
                services.AddSingleton(Options.Create(new AccessClientOptions()));
                services.AddSingleton<AccessCacheGeneration>();
                services.AddSingleton<ICacheInvalidator, NoInvalidator>();
            })
            .UseWolverine(options =>
            {
                options.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AccessChangedHandler));
                options.RetryAccessChangedCacheFailures(FastDelays);
            })
            .StartAsync(Ct);
        return (host, redis, host.Services.GetRequiredService<AccessCacheGeneration>());
    }

    [Fact]
    public async Task A_redis_failure_while_handling_AccessChanged_is_retried_until_the_generation_is_stored()
    {
        var (host, redis, _) = await StartAsync(failures: 1);
        using var _ = host;
        var tenant = $"t-{Guid.NewGuid():N}"[..16];

        await host.MessageBus().SendAsync(new AccessChanged(tenant, "Matrix", null, DateTimeOffset.UtcNow));

        // Stored in "Redis" only by a retry: the first attempt failed there.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!redis.HasGenerationOf(tenant) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }

        redis.FailedWrites.ShouldBe(1);
        redis.HasGenerationOf(tenant).ShouldBeTrue("the message went to the error queue instead of being retried");
        await host.StopAsync(Ct);
    }

    [Fact]
    public void Only_cache_write_failures_are_retried_by_this_rule()
    {
        AccessChangedRetryPolicy.IsCacheFailure(new FusionCacheDistributedCacheException("redis", new TimeoutException())).ShouldBeTrue();
        AccessChangedRetryPolicy.IsCacheFailure(new FusionCacheBackplaneException("backplane")).ShouldBeTrue();
        AccessChangedRetryPolicy.IsCacheFailure(new InvalidOperationException("bug")).ShouldBeFalse();
    }

    [Fact]
    public void Default_delays_are_bounded_and_grow()
    {
        AccessChangedRetryPolicy.Defaults().Delays().Select(d => d.TotalSeconds).ShouldBe([1, 5, 25, 125, 600]);
    }

    private sealed class NoInvalidator : ICacheInvalidator
    {
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => Task.CompletedTask;
    }

    /// <summary>An in-memory "Redis" whose first <c>failures</c> writes fail.</summary>
    private sealed class FlakyDistributedCache(int failures) : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _written = new();
        private int _failedWrites;

        public int FailedWrites => _failedWrites;

        // FusionCache may prefix the key (wire format version), so match its end.
        public bool HasGenerationOf(string tenant) => _written.Keys.Any(k => k.EndsWith($"access:gen:{tenant}", StringComparison.Ordinal));

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            FailWrite();
            _inner.Set(key, value, options);
            _written[key] = true;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            FailWrite();
            _written[key] = true;
            return _inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        private void FailWrite()
        {
            if (Interlocked.Increment(ref _failedWrites) <= failures)
            {
                throw new InvalidOperationException("Redis is unreachable.");
            }

            Interlocked.Decrement(ref _failedWrites);
        }
    }
}
