using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

/// <summary>
/// Open check from the MongoDB template (plan 03, Task 2): a service with <b>no message store</b> (Suppliers) handles
/// <c>AccessChanged</c> from a buffered RabbitMQ listener. When Redis fails while the new cache generation is stored, the
/// <c>RetryAccessChangedCacheFailures</c> rule must still retry (an in-memory scheduled retry, since there is no durable inbox)
/// instead of moving the message to the error queue on the first failure.
/// </summary>
public sealed class AccessEvictionRetryTests(RabbitFixture rabbit) : IClassFixture<RabbitFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_message_store_a_redis_failure_on_AccessChanged_is_retried_by_a_scheduled_retry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
                // Fast retries; the first write to "Redis" fails.
                [$"{AccessChangedRetryPolicy.SectionName}:Attempts"] = "3",
                [$"{AccessChangedRetryPolicy.SectionName}:InitialDelay"] = "00:00:00.200",
                [$"{AccessChangedRetryPolicy.SectionName}:Multiplier"] = "1",
            })
            .Build();
        var tenant = $"t-{Guid.NewGuid():N}"[..16];
        var service = $"retry-{Guid.NewGuid():N}"[..14];
        var redis = new AccessChangedRetryTests.FlakyDistributedCache(failures: 1);

        using var listener = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
                cache.SetupDistributedCache(redis, new FusionCacheSystemTextJsonSerializer());
                services.AddSingleton<IFusionCache>(cache);
                services.AddSingleton(Options.Create(new AccessClientOptions()));
                services.AddSingleton(Options.Create(new CachingOptions()));
                services.AddSingleton<AccessCacheGeneration>();
                services.AddSingleton<ICacheInvalidator, AccessChangedRetryTests.NoInvalidator>();
            })
            .UseWolverine(options => options.UseProcureAiAccessEviction(configuration, service, typeof(AccessEvictionRetryTests).Assembly))
            .StartAsync(Ct);

        using var access = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.Discovery.DisableConventionalDiscovery();
                options.UseProcureAiTransport(configuration);
                options.PublishMessage<AccessChanged>().ToRabbitExchange(Exchanges.Access);
            })
            .StartAsync(Ct);
        await access.MessageBus().PublishAsync(new AccessChanged(tenant, "Matrix", null, DateTimeOffset.UtcNow));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!redis.HasGenerationOf(tenant) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        redis.FailedWrites.ShouldBe(1, "the first attempt failed in Redis");
        redis.HasGenerationOf(tenant).ShouldBeTrue("the scheduled retry stored the generation; the message did not go to the error queue");
        await access.StopAsync(Ct);
        await listener.StopAsync(Ct);
    }
}
