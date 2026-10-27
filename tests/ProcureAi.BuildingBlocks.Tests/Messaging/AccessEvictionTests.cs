using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

/// <summary>
/// A service with no message store (MongoDB: Suppliers) listens to <c>AccessChanged</c> through <c>UseProcureAiAccessEviction</c>:
/// a buffered queue, no inbox. The event published by Access still advances the tenant's cache generation and evicts the tagged entries,
/// including the eviction that is published from inside the handler through <see cref="MessageCacheInvalidator"/>.
/// </summary>
public sealed class AccessEvictionTests(RabbitFixture rabbit) : IClassFixture<RabbitFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_service_without_a_message_store_still_evicts_on_AccessChanged()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString })
            .Build();
        var tenant = $"t-{Guid.NewGuid():N}"[..16];
        var service = $"evict-{Guid.NewGuid():N}"[..14];

        using var listener = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFusionCache>(new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions())));
                services.AddSingleton(Options.Create(new AccessClientOptions()));
                services.AddSingleton(Options.Create(new CachingOptions()));
                services.AddSingleton<AccessCacheGeneration>();
                services.AddScoped<ICacheInvalidator, MessageCacheInvalidator>();
            })
            .UseWolverine(options => options.UseProcureAiAccessEviction(configuration, service, typeof(AccessEvictionTests).Assembly))
            .StartAsync(Ct);
        var cache = listener.Services.GetRequiredService<IFusionCache>();
        var generation = listener.Services.GetRequiredService<AccessCacheGeneration>();
        var otherTenant = $"o-{Guid.NewGuid():N}"[..16];
        await cache.SetAsync("entry:mine", "cached", tags: [AccessCacheTag.Tenant(tenant)], token: Ct);
        await cache.SetAsync("entry:other", "cached", tags: [AccessCacheTag.Tenant(otherTenant)], token: Ct);
        (await generation.GetAsync(tenant, Ct)).ShouldBe(AccessCacheGeneration.Initial);

        // Access publishes to the exchange; the listener's queue (bound at startup) receives it.
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
        while (DateTime.UtcNow < deadline
            && (await generation.GetAsync(tenant, Ct) == AccessCacheGeneration.Initial
                || (await cache.TryGetAsync<string>("entry:mine", token: Ct)).HasValue))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        (await generation.GetAsync(tenant, Ct)).ShouldNotBe(AccessCacheGeneration.Initial, "the generation advanced");
        (await cache.TryGetAsync<string>("entry:mine", token: Ct)).HasValue.ShouldBeFalse("the tenant's tagged entry was evicted");
        (await cache.TryGetAsync<string>("entry:other", token: Ct)).HasValue.ShouldBeTrue("another tenant's entry stays");
        (await generation.GetAsync(otherTenant, Ct)).ShouldBe(AccessCacheGeneration.Initial);

        await access.StopAsync(Ct);
        await listener.StopAsync(Ct);
    }
}
