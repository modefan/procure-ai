using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Presence;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// Presence end to end (plan 04 ruling 11, Review Focus 4 and 5): <c>Watch</c>/<c>Unwatch</c>/<c>Heartbeat</c> on the real hub, the
/// state in the shared Redis, <c>ViewersChanged</c> and <c>OnlineChanged</c> across instances through the backplane. Each test uses
/// tenants of its own.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class PresenceTests(NotificationsApp app)
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private static string Token(string tenant, string user, string? name) =>
        TestAuth.CreateToken(user, tenant, name is null ? null : new Dictionary<string, object> { ["name"] = name }, "requester");

    private static Task<LiveClient> ConnectAsync(TestServer server, string tenant, string user, string? name) =>
        LiveClient.ConnectAsync(server, tenant, Token(tenant, user, name));

    private static bool ViewersOf(LivePush push, string id, params string[] users) =>
        push.Method == LiveHubContract.ViewersChangedMethod && push.Entity == LiveEntities.Request && push.Id == id
        && push.UserIds.SequenceEqual(users);

    private static bool Online(LivePush push, params string[] users) =>
        push.Method == LiveHubContract.OnlineChangedMethod && push.UserIds.SequenceEqual(users);

    private static Task<Viewer[]> WatchAsync(LiveClient client, string id) =>
        client.Connection.InvokeAsync<Viewer[]>(LiveHubContract.WatchMethod, LiveEntities.Request, id);

    [Fact]
    public async Task Watch_broadcasts_distinct_viewers()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var maria = await ConnectAsync(app.A.Server, tenant, "maria", "Maria Manager");
        await using var ben = await ConnectAsync(app.B.Server, tenant, "ben", "Ben Buyer");

        (await WatchAsync(maria, id)).ShouldBe([new Viewer("maria", "Maria Manager")]);
        var returned = await WatchAsync(ben, id);

        // Maria (instance A) hears that Ben (instance B) joined; the list is sorted by name; the wire format is { userId, name }.
        var push = await maria.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival);
        push.Payload[0].EnumerateObject().Select(p => p.Name).ShouldBe(["userId", "name"]);
        push.Payload[0].GetProperty("name").GetString().ShouldBe("Ben Buyer");
        returned.ShouldBe([new Viewer("ben", "Ben Buyer"), new Viewer("maria", "Maria Manager")]);
        await maria.WaitForPushAsync(p => Online(p, "ben", "maria"), Arrival);
    }

    [Fact]
    public async Task Second_tab_of_same_user_counts_once()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var tab1 = await ConnectAsync(app.A.Server, tenant, "maria", "Maria Manager");
        await using var tab2 = await ConnectAsync(app.B.Server, tenant, "maria", "Maria Manager");
        // No name claim: shown under the user ID.
        await using var ben = await ConnectAsync(app.A.Server, tenant, "ben", name: null);

        await WatchAsync(tab1, id);
        await WatchAsync(tab2, id);
        var viewers = await WatchAsync(ben, id);

        viewers.ShouldBe([new Viewer("ben", "ben"), new Viewer("maria", "Maria Manager")]);
        await ben.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival);
        await ben.WaitForPushAsync(p => Online(p, "ben", "maria"), Arrival);

        // One tab closed: the lists are sent again, and she still views it and is online.
        var mark = ben.PushCount;
        await tab1.DisposeAsync();
        await ben.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival, after: mark);
        await ben.WaitForPushAsync(p => Online(p, "ben", "maria"), Arrival, after: mark);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        ben.Pushes.Last(p => p.Method == LiveHubContract.ViewersChangedMethod).UserIds.ShouldBe(["ben", "maria"]);
    }

    [Fact]
    public async Task Disconnect_removes_viewer()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var maria = await ConnectAsync(app.A.Server, tenant, "maria", "Maria Manager");
        var ben = await ConnectAsync(app.B.Server, tenant, "ben", "Ben Buyer");
        await WatchAsync(maria, id);
        await WatchAsync(ben, id);
        await maria.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival);

        var mark = maria.PushCount;

        await ben.DisposeAsync();

        await maria.WaitForPushAsync(p => ViewersOf(p, id, "maria"), Arrival, after: mark);
        await maria.WaitForPushAsync(p => Online(p, "maria"), Arrival, after: mark);
    }

    [Fact]
    public async Task Unwatch_removes_viewer()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var maria = await ConnectAsync(app.A.Server, tenant, "maria", "Maria Manager");
        await using var ben = await ConnectAsync(app.B.Server, tenant, "ben", "Ben Buyer");
        await WatchAsync(maria, id);
        await WatchAsync(ben, id);
        await maria.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival);
        var mark = maria.PushCount;

        await ben.Connection.InvokeAsync(LiveHubContract.UnwatchMethod, LiveEntities.Request, id);

        await maria.WaitForPushAsync(p => ViewersOf(p, id, "maria"), Arrival, after: mark);
    }

    [Fact]
    public async Task Missed_heartbeats_drop_viewer_within_ttl_on_other_instance()
    {
        // Two more instances on one fake presence clock: P never sweeps (its interval is a day), Q sweeps every 15 s of that clock.
        // Ben is connected to P and stops sending heartbeats (a laptop gone to sleep); Q drops him and tells Maria.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var p = new NotificationsApiFactory(app, builder =>
        {
            builder.UseSetting("Notifications:Presence:SweepInterval", "1.00:00:00");
            builder.ConfigureTestServices(services => services.AddKeyedSingleton<TimeProvider>(PresenceClock.Key, clock));
        });
        await using var q = new NotificationsApiFactory(app, builder =>
            builder.ConfigureTestServices(services => services.AddKeyedSingleton<TimeProvider>(PresenceClock.Key, clock)));
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var ben = await ConnectAsync(p.Server, tenant, "ben", "Ben Buyer");
        await using var maria = await ConnectAsync(q.Server, tenant, "maria", "Maria Manager");
        await WatchAsync(ben, id);
        await WatchAsync(maria, id);
        await maria.WaitForPushAsync(push => ViewersOf(push, id, "ben", "maria"), Arrival);

        // 30 s: nobody is stale yet. Maria's client sends its heartbeat; Ben's does not.
        clock.Advance(TimeSpan.FromSeconds(30));
        await maria.Connection.InvokeAsync(LiveHubContract.HeartbeatMethod);
        maria.Pushes.Last(push => push.Method == LiveHubContract.ViewersChangedMethod).UserIds.ShouldBe(["ben", "maria"]);

        // 60 s: Ben's last heartbeat is more than the 45 s TTL ago. Q's next sweep drops him (P's connection, Q's sweeper).
        var mark = maria.PushCount;
        clock.Advance(TimeSpan.FromSeconds(30));
        await maria.WaitForPushAsync(push => ViewersOf(push, id, "maria"), Arrival, after: mark);
        await maria.WaitForPushAsync(push => Online(push, "maria"), Arrival, after: mark);
    }

    [Fact]
    public async Task Watch_never_crosses_tenants()
    {
        var acme = NewTenant();
        var globex = NewTenant();
        var id = Guid.NewGuid().ToString();
        await using var maria = await ConnectAsync(app.A.Server, acme, "maria", "Maria Manager");
        await using var gina = await ConnectAsync(app.B.Server, globex, "gina", "Gina Globex");
        // A user token cannot pick a tenant: X-Tenant-Id counts only with the admin key.
        await using var sneaky = await LiveClient.ConnectAsync(
            app.A.Server, globex, Token(globex, "sid", "Sid Sneaky"), new Dictionary<string, string> { ["X-Tenant-Id"] = acme });

        await WatchAsync(maria, id);
        (await WatchAsync(gina, id)).ShouldBe([new Viewer("gina", "Gina Globex")]);
        (await WatchAsync(sneaky, id)).ShouldBe([new Viewer("gina", "Gina Globex"), new Viewer("sid", "Sid Sneaky")]);
        await using var ben = await ConnectAsync(app.B.Server, acme, "ben", "Ben Buyer");
        await WatchAsync(ben, id);
        await maria.WaitForPushAsync(p => ViewersOf(p, id, "ben", "maria"), Arrival);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Globex connections never saw an Acme user, in viewer lists or online lists.
        foreach (var client in new[] { gina, sneaky })
        {
            client.Pushes.Where(p => p.Method is LiveHubContract.ViewersChangedMethod or LiveHubContract.OnlineChangedMethod)
                .SelectMany(p => p.UserIds).ShouldAllBe(u => u == "gina" || u == "sid");
        }

        maria.Pushes.Where(p => p.Method is LiveHubContract.ViewersChangedMethod or LiveHubContract.OnlineChangedMethod)
            .SelectMany(p => p.UserIds).ShouldAllBe(u => u == "maria" || u == "ben");
    }

    [Fact]
    public async Task A_presence_failure_on_connect_keeps_the_connection_and_the_next_heartbeat_brings_it_online()
    {
        // An instance whose presence store fails on the first write (Redis away for a moment).
        await using var flaky = new NotificationsApiFactory(app, builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IPresenceStore>(provider => new FailOnceStore(
                ActivatorUtilities.CreateInstance<ProcureAi.Notifications.Infrastructure.Presence.RedisPresenceStore>(provider)))));
        var tenant = NewTenant();

        // Connected anyway: it is in the tenant group (ConnectAsync waits for a live push to arrive).
        await using var ben = await ConnectAsync(flaky.Server, tenant, "ben", "Ben Buyer");
        ben.Pushes.ShouldNotContain(p => p.Method == LiveHubContract.OnlineChangedMethod, "presence failed on connect");

        await ben.Connection.InvokeAsync(LiveHubContract.HeartbeatMethod);

        await ben.WaitForPushAsync(p => Online(p, "ben"), Arrival);
    }

    [Theory]
    [InlineData("tenant", "acme")]
    [InlineData("request", "acme:request:1")]
    [InlineData("request", "")]
    public async Task Watch_refuses_unknown_entities_and_ids_that_are_not_plain(string entity, string id)
    {
        var tenant = NewTenant();
        await using var maria = await ConnectAsync(app.A.Server, tenant, "maria", "Maria Manager");

        await Should.ThrowAsync<HubException>(() => maria.Connection.InvokeAsync<Viewer[]>(LiveHubContract.WatchMethod, entity, id));
    }

    /// <summary>The real store, but the first write throws as Redis does when it is briefly away.</summary>
    private sealed class FailOnceStore(IPresenceStore inner) : IPresenceStore
    {
        private int _failed;

        public Task<Upserted> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
            Interlocked.Exchange(ref _failed, 1) == 0
                ? throw new StackExchange.Redis.RedisConnectionException(StackExchange.Redis.ConnectionFailureType.SocketFailure, StackExchange.Redis.CommandFlags.None, "Redis away")
                : inner.UpsertAsync(connection, now, cancellationToken);

        public Task<bool> WatchAsync(
            PresenceConnection connection, PresenceTarget target, DateTimeOffset now, int maxWatches, CancellationToken cancellationToken) =>
            inner.WatchAsync(connection, target, now, maxWatches, cancellationToken);

        public Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken) =>
            inner.UnwatchAsync(connection, target, cancellationToken);

        public Task<RemovedConnection?> RemoveAsync(string connectionId, CancellationToken cancellationToken) =>
            inner.RemoveAsync(connectionId, cancellationToken);

        public Task<RemovedConnection?> RemoveIfStaleAsync(StaleConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            inner.RemoveIfStaleAsync(connection, cutoff, cancellationToken);

        public Task<IReadOnlyList<PresenceEntry>> OnlineAsync(string tenantId, CancellationToken cancellationToken) =>
            inner.OnlineAsync(tenantId, cancellationToken);

        public Task<IReadOnlyList<PresenceEntry>> ViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken) =>
            inner.ViewersAsync(tenantId, target, cancellationToken);

        public Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            inner.StaleAsync(cutoff, cancellationToken);
    }
}
