using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.Contracts.Access;
using Shouldly;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>
/// The tenant generation of cached Access answers (final review I-4): a failure to store a new generation must reach the
/// message handler (so Wolverine retries instead of acking), and an instance that missed the backplane message must pick up the
/// new generation from the distributed cache within a few seconds, not hours.
/// </summary>
public sealed class AccessCacheGenerationTests
{
    private static readonly TimeSpan MemoryDuration = TimeSpan.FromMilliseconds(200);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FusionCache Cache(IDistributedCache distributed, IFusionCacheBackplane? backplane = null)
    {
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        cache.SetupDistributedCache(distributed, new FusionCacheSystemTextJsonSerializer());
        if (backplane is not null)
        {
            cache.SetupBackplane(backplane);
        }

        return cache;
    }

    private static AccessCacheGeneration Generation(IFusionCache cache) =>
        new(cache, Options.Create(new AccessClientOptions { GenerationMemoryCacheDuration = MemoryDuration }));

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task Advance_throws_when_the_distributed_cache_fails()
    {
        using var cache = Cache(new SwitchableDistributedCache { Failing = true });

        await Should.ThrowAsync<Exception>(async () => await Generation(cache).AdvanceAsync(NewTenant(), Ct));
    }

    [Fact]
    public async Task Advance_throws_when_the_backplane_fails()
    {
        using var cache = Cache(new SwitchableDistributedCache(), new FailingBackplane());

        await Should.ThrowAsync<Exception>(async () => await Generation(cache).AdvanceAsync(NewTenant(), Ct));
    }

    [Fact]
    public async Task AccessChanged_handler_fails_when_the_generation_cannot_be_stored_so_the_message_is_retried()
    {
        using var cache = Cache(new SwitchableDistributedCache { Failing = true });

        await Should.ThrowAsync<Exception>(() => AccessChangedHandler.Handle(
            new AccessChanged(NewTenant(), "Matrix", null, DateTimeOffset.UtcNow), Generation(cache), new NoInvalidator()));
    }

    [Fact]
    public async Task An_instance_that_missed_the_backplane_sees_the_new_generation_after_the_memory_duration()
    {
        var shared = new SwitchableDistributedCache();
        using var cacheA = Cache(shared);
        using var cacheB = Cache(shared);
        var a = Generation(cacheA);
        var b = Generation(cacheB);
        var tenant = NewTenant();
        await a.AdvanceAsync(tenant, Ct);
        var before = await b.GetAsync(tenant, Ct);

        // No backplane: B hears nothing. It must still find the new generation in the distributed cache soon.
        await a.AdvanceAsync(tenant, Ct);
        var current = await a.GetAsync(tenant, Ct);
        current.ShouldNotBe(before);
        await Task.Delay(MemoryDuration * 3, Ct);

        (await b.GetAsync(tenant, Ct)).ShouldBe(current);
    }

    [Fact]
    public async Task A_distributed_cache_outage_after_the_memory_duration_keeps_the_last_known_generation()
    {
        // Falling back to the initial generation would make answers cached under it (before any change) reachable again.
        var shared = new SwitchableDistributedCache();
        using var cache = Cache(shared);
        var generation = Generation(cache);
        var tenant = NewTenant();
        await generation.AdvanceAsync(tenant, Ct);
        var known = await generation.GetAsync(tenant, Ct);
        known.ShouldNotBe(AccessCacheGeneration.Initial);

        shared.Failing = true;
        await Task.Delay(MemoryDuration * 3, Ct);

        (await generation.GetAsync(tenant, Ct)).ShouldBe(known);
    }

    private sealed class NoInvalidator : ICacheInvalidator
    {
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => Task.CompletedTask;
    }

    /// <summary>An in-memory distributed cache that can be made to fail like an unreachable Redis.</summary>
    private sealed class SwitchableDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

        public bool Failing { get; set; }

        public byte[]? Get(string key) => Check()._inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Check()._inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Check()._inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            Check()._inner.SetAsync(key, value, options, token);

        public void Refresh(string key) => Check()._inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => Check()._inner.RefreshAsync(key, token);

        public void Remove(string key) => Check()._inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => Check()._inner.RemoveAsync(key, token);

        private SwitchableDistributedCache Check() =>
            Failing ? throw new InvalidOperationException("The distributed cache is unreachable.") : this;
    }

    private sealed class FailingBackplane : IFusionCacheBackplane
    {
        public void Subscribe(BackplaneSubscriptionOptions options)
        {
        }

        public ValueTask SubscribeAsync(BackplaneSubscriptionOptions options) => ValueTask.CompletedTask;

        public void Unsubscribe()
        {
        }

        public ValueTask UnsubscribeAsync() => ValueTask.CompletedTask;

        public ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("The backplane is unreachable.");

        public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("The backplane is unreachable.");
    }
}
