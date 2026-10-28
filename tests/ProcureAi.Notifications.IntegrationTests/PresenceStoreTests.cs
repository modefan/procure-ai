using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Infrastructure.Email;
using ProcureAi.Notifications.Infrastructure.Presence;
using Shouldly;
using StackExchange.Redis;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The Redis presence store and email dedup against the real Redis (Lua scripts included), driven step by step: what happens when a
/// heartbeat lands between the sweeper's stale read and its removal, and what a swept connection gets back with its next heartbeat.
/// Each test uses a tenant of its own.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class PresenceStoreTests(NotificationsApp app) : IAsyncLifetime
{
    private static readonly PresenceTarget Target = new(LiveEntities.Request, Guid.NewGuid().ToString());

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly RecordingPushes _pushes = new();
    private ConnectionMultiplexer _redis = null!;
    private HookedStore _store = null!;
    private PresenceService _presence = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(app.Redis.ConnectionString);
        var options = Options.Create(new PresenceOptions());
        _store = new HookedStore(new RedisPresenceStore(_redis, options));
        _presence = new PresenceService(_store, _pushes, options, _clock, NullLogger<PresenceService>.Instance);
    }

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync();

    private static PresenceConnection Ben(string tenant) => new($"conn-{Guid.NewGuid():N}", tenant, "ben", "Ben Buyer");

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task A_heartbeat_between_the_stale_read_and_the_removal_keeps_the_connection_and_its_views()
    {
        var tenant = NewTenant();
        var ben = Ben(tenant);
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(ben, Target, Ct);
        _clock.Advance(TimeSpan.FromSeconds(46));
        // The sweep reads Ben as stale; his heartbeat lands before the removal.
        _store.AfterStaleRead = () => _presence.HeartbeatAsync(ben, Ct);

        (await _presence.SweepAsync(Ct)).ShouldBe(0, "the removal re-checked the heartbeat");

        (await _presence.OnlineAsync(tenant, Ct)).ShouldBe([new Viewer("ben", "Ben Buyer")]);
        (await _store.ViewersAsync(tenant, Target, Ct)).Select(e => e.ConnectionId).ShouldBe([ben.ConnectionId]);
        (await _redis.GetDatabase().SetMembersAsync($"presence:conn:{ben.ConnectionId}:views")).Select(v => v.ToString())
            .ShouldBe([Target.Key]);
    }

    [Fact]
    public async Task A_swept_connection_gets_its_watches_back_with_its_next_heartbeat()
    {
        var tenant = NewTenant();
        var ben = Ben(tenant);
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(ben, Target, Ct);
        _clock.Advance(TimeSpan.FromSeconds(46));

        (await _presence.SweepAsync(Ct)).ShouldBe(1);
        (await _store.ViewersAsync(tenant, Target, Ct)).ShouldBeEmpty();
        (await _presence.OnlineAsync(tenant, Ct)).ShouldBeEmpty();

        // The laptop wakes up: the same connection sends its heartbeat.
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _presence.HeartbeatAsync(ben, Ct);

        (await _presence.OnlineAsync(tenant, Ct)).ShouldBe([new Viewer("ben", "Ben Buyer")]);
        (await _store.ViewersAsync(tenant, Target, Ct)).Select(e => e.ConnectionId).ShouldBe([ben.ConnectionId]);
        _pushes.Viewers.Last().ShouldBe((tenant, Target, "ben"));
    }

    [Fact]
    public async Task A_tenant_is_forgotten_only_while_it_has_no_connection()
    {
        var tenant = NewTenant();
        var ben = Ben(tenant);
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.DisconnectedAsync(ben.ConnectionId, Ct);
        var db = _redis.GetDatabase();
        (await db.SetContainsAsync("presence:tenants", tenant)).ShouldBeTrue();

        // A connection arrives just before the clean-up: the tenant stays.
        var maria = new PresenceConnection($"conn-{Guid.NewGuid():N}", tenant, "maria", "Maria Manager");
        await _presence.ConnectedAsync(maria, Ct);
        await _presence.SweepAsync(Ct);
        (await db.SetContainsAsync("presence:tenants", tenant)).ShouldBeTrue();

        await _presence.DisconnectedAsync(maria.ConnectionId, Ct);
        await _presence.SweepAsync(Ct);
        (await db.SetContainsAsync("presence:tenants", tenant)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_email_claim_expires_unless_the_send_confirms_it()
    {
        var once = new RedisEmailOnce(_redis);
        var unsent = $"email:{Guid.NewGuid():N}:maria";
        var sent = $"email:{Guid.NewGuid():N}:maria";

        // An instance claimed and died before sending: the claim lapses, so the redelivery can send.
        (await once.TryClaimAsync(unsent, TimeSpan.FromSeconds(1), Ct)).ShouldBeTrue();
        (await once.TryClaimAsync(unsent, TimeSpan.FromSeconds(1), Ct)).ShouldBeFalse("a concurrent delivery skips it");
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        (await once.TryClaimAsync(unsent, TimeSpan.FromSeconds(1), Ct)).ShouldBeTrue();

        // Sent and confirmed: kept for the dedup window.
        (await once.TryClaimAsync(sent, TimeSpan.FromSeconds(1), Ct)).ShouldBeTrue();
        await once.ConfirmAsync(sent, TimeSpan.FromDays(7), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        (await once.TryClaimAsync(sent, TimeSpan.FromSeconds(1), Ct)).ShouldBeFalse();
        (await _redis.GetDatabase().KeyTimeToLiveAsync(sent)).ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.FromDays(6));
    }

    /// <summary>The real store with a hook right after the sweeper's stale read.</summary>
    private sealed class HookedStore(IPresenceStore inner) : IPresenceStore
    {
        public Func<Task>? AfterStaleRead { get; set; }

        public Task<Upserted> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.UpsertAsync(connection, now, cancellationToken);

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

        public async Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            var stale = await inner.StaleAsync(cutoff, cancellationToken);
            if (AfterStaleRead is { } hook)
            {
                AfterStaleRead = null;
                await hook();
            }

            return stale;
        }
    }

    private sealed class RecordingPushes : ILivePushes
    {
        public List<(string Tenant, PresenceTarget Target, string Users)> Viewers { get; } = [];

        public Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken)
        {
            Viewers.Add((tenantId, target, string.Join(',', viewers.Select(v => v.UserId))));
            return Task.CompletedTask;
        }

        public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
