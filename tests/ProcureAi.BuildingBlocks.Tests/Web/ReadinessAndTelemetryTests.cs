using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Health;
using Shouldly;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Nothing listens on port 1: stands in for a dependency that is down.</summary>
[Collection(RedisCollection.Name)]
public sealed class ReadinessTests(RedisFixture redis)
{
    private const string Down = "127.0.0.1:1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpStatusCode> ReadyAsync(WebApplication app)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health/ready", Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Redis_up_is_ready_200()
    {
        await using var app = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = redis.ConnectionString },
            configure: b => { b.AddProcureAiCaching(); b.Services.AddRedisReadiness(); });

        (await ReadyAsync(app)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Redis_down_is_not_ready_503_but_still_live()
    {
        await using var app = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = Down },
            configure: b => { b.AddProcureAiCaching(); b.Services.AddRedisReadiness(); });

        (await ReadyAsync(app)).ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var client = app.GetTestClient();
        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Postgres_down_is_not_ready_503()
    {
        await using var app = await TestHost.StartAsync(
            configure: b => b.Services.AddPostgresReadiness("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=2"));

        (await ReadyAsync(app)).ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task RabbitMq_down_is_not_ready_503()
    {
        await using var app = await TestHost.StartAsync(
            configure: b => b.Services.AddRabbitMqReadiness("amqp://x:x@127.0.0.1:1"));

        (await ReadyAsync(app)).ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}

[Collection(RedisCollection.Name)]
public sealed class TelemetryAndCacheNameTests(RedisFixture redis)
{
    [Theory]
    [InlineData("Wolverine")]
    [InlineData("Npgsql")]
    [InlineData("ZiggyCreatures.Caching.Fusion")]
    public async Task Service_defaults_subscribe_to_the_library_activity_sources(string source)
    {
        await using var app = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = redis.ConnectionString },
            configure: b => b.AddProcureAiCaching());
        _ = app;

        // The tracer provider adds a listener for every source it subscribes to; one nobody subscribed to has none.
        using var probe = new ActivitySource(source);
        probe.HasListeners().ShouldBeTrue($"no tracing subscription for '{source}'");
    }

    [Fact]
    public async Task Service_defaults_subscribe_to_the_application_activity_source()
    {
        await using var app = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = redis.ConnectionString }, configure: b => b.AddProcureAiCaching());

        using var probe = new ActivitySource(app.Environment.ApplicationName);
        probe.HasListeners().ShouldBeTrue();
    }

    /// <summary>The Redis pub/sub channels FusionCache's backplane has subscribed to (the first use connects it).</summary>
    private static async Task<string[]> BackplaneChannelsAsync(WebApplication app)
    {
        await app.Services.GetRequiredService<IFusionCache>().SetAsync("probe", 1, token: TestContext.Current.CancellationToken);
        var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
        var channels = await redis.GetServer(redis.GetEndPoints()[0]).SubscriptionChannelsAsync();
        return channels.Select(c => c.ToString()).ToArray();
    }

    [Fact]
    public async Task FusionCache_backplane_prefix_defaults_to_the_application_name_and_can_be_configured()
    {
        await using var defaults = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = redis.ConnectionString }, configure: b => b.AddProcureAiCaching());
        await using var named = await TestHost.StartAsync(
            new() { ["ConnectionStrings:Redis"] = redis.ConnectionString, ["FusionCache:CacheName"] = "requests-cache" },
            configure: b => b.AddProcureAiCaching());

        (await BackplaneChannelsAsync(defaults)).ShouldContain(channel => channel.Contains(defaults.Environment.ApplicationName));
        (await BackplaneChannelsAsync(named)).ShouldContain(channel => channel.Contains("requests-cache"));
    }
}
