using System.Net;
using Shouldly;
using StackExchange.Redis;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>The nightly demo reset of Notifications: every user's bell goes, so nobody is told about a request that no longer exists.</summary>
[Collection(NotificationsCollection.Name)]
public sealed class DemoResetTests(NotificationsApp app)
{
    [Fact]
    public async Task Demo_reset_clears_every_bell()
    {
        await using var factory = new NotificationsApiFactory(app, builder => builder.UseSetting("DemoReset:Enabled", "true"));
        using var client = factory.CreateClient();
        var redis = await ConnectionMultiplexer.ConnectAsync(app.Redis.ConnectionString);
        await using var _ = redis;
        var keys = new RedisKey[] { "bell:acme:tom:index", "bell:acme:tom:items", "bell:globex:ben:read" };
        foreach (var key in keys)
        {
            await redis.GetDatabase().StringSetAsync(key, "x");
        }

        await redis.GetDatabase().StringSetAsync("unrelated:demo-reset-test", "kept");

        (await DemoResetProbe.ResetAsync(client, "notifications")).ShouldBe(HttpStatusCode.NoContent);

        foreach (var key in keys)
        {
            (await redis.GetDatabase().KeyExistsAsync(key)).ShouldBeFalse(key.ToString());
        }

        (await redis.GetDatabase().KeyExistsAsync("unrelated:demo-reset-test")).ShouldBeTrue("only the bell is reset");
        await redis.GetDatabase().KeyDeleteAsync("unrelated:demo-reset-test");
    }
}
