using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Web.Options;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

public class EvictCacheTagsHandlerTests
{
    private sealed class Invalidator(Func<IEnumerable<string>, CancellationToken, Task> remove) : ICacheInvalidator
    {
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) => remove(tags, cancellationToken);

        public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => throw new NotSupportedException();
    }

    private static IOptions<CachingOptions> Options(TimeSpan timeout) =>
        Microsoft.Extensions.Options.Options.Create(new CachingOptions { InvalidationTimeout = timeout });

    private static Task HandleAsync(ICacheInvalidator cache, TimeSpan timeout) =>
        EvictCacheTagsHandler.Handle(new EvictCacheTags(["a", "b"]), cache, Options(timeout), NullLogger<EvictCacheTags>.Instance);

    [Fact]
    public async Task Evicts_every_tag()
    {
        var seen = new List<string>();

        await HandleAsync(new Invalidator((tags, _) => { seen.AddRange(tags); return Task.CompletedTask; }), TimeSpan.FromSeconds(2));

        seen.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_failing_eviction_is_logged_not_raised()
    {
        await HandleAsync(new Invalidator((_, _) => throw new InvalidOperationException("redis down")), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_slow_eviction_is_cancelled_at_the_timeout_and_not_raised()
    {
        var cancelled = false;
        var slow = new Invalidator(async (_, token) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        await HandleAsync(slow, TimeSpan.FromMilliseconds(100));

        cancelled.ShouldBeTrue();
    }

    [Fact]
    public void Default_timeout_is_two_seconds() => new CachingOptions().InvalidationTimeout.ShouldBe(TimeSpan.FromSeconds(2));
}
