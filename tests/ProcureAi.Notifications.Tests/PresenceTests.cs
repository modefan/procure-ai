using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Domain;
using Shouldly;

namespace ProcureAi.Notifications.Tests;

/// <summary>
/// Presence rules on a fake clock (plan 04 ruling 11): distinct users, the TTL, the sweep, what is sent to whom, and what a client may
/// watch. The store here keeps the same data as the Redis one in memory; the integration tests run the real one.
/// </summary>
public sealed class PresenceTests
{
    private const string Acme = "acme";
    private const string Globex = "globex";
    private static readonly PresenceTarget Pr1043 = new(LiveEntities.Request, "11111111-1111-1111-1111-111111111111");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly MemoryPresenceStore _store = new();
    private readonly RecordingPushes _pushes = new();
    private readonly PresenceService _presence;

    public PresenceTests() =>
        _presence = new PresenceService(_store, _pushes, Options.Create(new PresenceOptions()), _clock, NullLogger<PresenceService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PresenceConnection Conn(string id, string user, string name, string tenant = Acme) => new(id, tenant, user, name);

    [Fact]
    public async Task Watch_broadcasts_distinct_viewers()
    {
        var maria = Conn("c1", "maria", "Maria Naidoo");
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(maria, Ct);
        await _presence.ConnectedAsync(ben, Ct);

        await _presence.WatchAsync(maria, Pr1043, Ct);
        var returned = await _presence.WatchAsync(ben, Pr1043, Ct);

        var last = _pushes.Viewers.Last();
        last.Tenant.ShouldBe(Acme);
        last.Target.ShouldBe(Pr1043);
        last.Viewers.ShouldBe([new Viewer("ben", "Ben Buyer"), new Viewer("maria", "Maria Naidoo")]);
        returned.ShouldBe(last.Viewers);
    }

    [Fact]
    public async Task Second_tab_of_same_user_counts_once()
    {
        var tab1 = Conn("c1", "maria", "Maria Naidoo");
        var tab2 = Conn("c2", "maria", "Maria Naidoo");
        await _presence.ConnectedAsync(tab1, Ct);
        await _presence.ConnectedAsync(tab2, Ct);
        await _presence.WatchAsync(tab1, Pr1043, Ct);
        await _presence.WatchAsync(tab2, Pr1043, Ct);

        _pushes.Viewers.Last().Viewers.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _pushes.Online.Last().Online.ShouldBe([new Viewer("maria", "Maria Naidoo")]);

        // Closing one tab leaves her viewing and online.
        await _presence.DisconnectedAsync("c1", Ct);
        _pushes.Viewers.Last().Viewers.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _pushes.Online.Last().Online.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
    }

    [Fact]
    public async Task Disconnect_removes_viewer_and_online_user()
    {
        var maria = Conn("c1", "maria", "Maria Naidoo");
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(maria, Ct);
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(maria, Pr1043, Ct);
        await _presence.WatchAsync(ben, Pr1043, Ct);

        await _presence.DisconnectedAsync("c2", Ct);

        _pushes.Viewers.Last().Viewers.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _pushes.Online.Last().Online.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
    }

    [Fact]
    public async Task Missed_heartbeats_drop_the_connection_after_the_ttl_and_not_before()
    {
        var maria = Conn("c1", "maria", "Maria Naidoo");
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(maria, Ct);
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(maria, Pr1043, Ct);
        await _presence.WatchAsync(ben, Pr1043, Ct);

        // Maria keeps sending heartbeats every 15 s; Ben's laptop went to sleep.
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _presence.HeartbeatAsync(maria, Ct);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _presence.HeartbeatAsync(maria, Ct);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _presence.HeartbeatAsync(maria, Ct);
        (await _presence.SweepAsync(Ct)).ShouldBe(0, "exactly the TTL since Ben's last heartbeat still counts");
        (await _presence.OnlineAsync(Acme, Ct)).Select(v => v.UserId).ShouldBe(["ben", "maria"]);

        _clock.Advance(TimeSpan.FromSeconds(1));
        // Readers already leave him out before the sweep...
        (await _presence.OnlineAsync(Acme, Ct)).Select(v => v.UserId).ShouldBe(["maria"]);
        // ...and the sweep removes him and tells the others.
        (await _presence.SweepAsync(Ct)).ShouldBe(1);
        _pushes.Viewers.Last().Viewers.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _pushes.Online.Last().Online.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _store.Known("c2").ShouldBeFalse();
    }

    [Fact]
    public async Task A_swept_connection_that_sends_a_heartbeat_comes_back_online_and_viewing()
    {
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(ben, Pr1043, Ct);
        _clock.Advance(TimeSpan.FromSeconds(46));
        (await _presence.SweepAsync(Ct)).ShouldBe(1);
        _pushes.Viewers.Last().Viewers.ShouldBeEmpty();
        var pushesBefore = _pushes.Online.Count;

        await _presence.HeartbeatAsync(ben, Ct);

        _pushes.Online.Count.ShouldBe(pushesBefore + 1);
        _pushes.Online.Last().Online.ShouldBe([new Viewer("ben", "Ben Buyer")]);
        // The sweep kept what he watches: he is a viewer again, and the viewers are told.
        _pushes.Viewers.Last().Target.ShouldBe(Pr1043);
        _pushes.Viewers.Last().Viewers.ShouldBe([new Viewer("ben", "Ben Buyer")]);
    }

    [Fact]
    public async Task A_heartbeat_between_the_stale_read_and_the_removal_keeps_the_connection_and_its_watches()
    {
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(ben, Ct);
        await _presence.WatchAsync(ben, Pr1043, Ct);
        _clock.Advance(TimeSpan.FromSeconds(46));
        // The sweep reads Ben as stale; his heartbeat lands before it removes him.
        _store.AfterStaleRead = () => _store.UpsertAsync(ben, _clock.GetUtcNow(), Ct).GetAwaiter().GetResult();

        (await _presence.SweepAsync(Ct)).ShouldBe(0);

        (await _presence.OnlineAsync(Acme, Ct)).ShouldBe([new Viewer("ben", "Ben Buyer")]);
        (await _store.ViewersAsync(Acme, Pr1043, Ct)).Select(e => e.UserId).ShouldBe(["ben"]);
    }

    [Fact]
    public async Task A_heartbeat_of_a_known_connection_sends_nothing()
    {
        var ben = Conn("c2", "ben", "Ben Buyer");
        await _presence.ConnectedAsync(ben, Ct);
        var pushesBefore = _pushes.Online.Count;

        await _presence.HeartbeatAsync(ben, Ct);

        _pushes.Online.Count.ShouldBe(pushesBefore);
    }

    [Fact]
    public async Task Watch_never_crosses_tenants()
    {
        var acme = Conn("c1", "maria", "Maria Naidoo");
        var globex = Conn("c2", "gina", "Gina Globex", Globex);
        await _presence.ConnectedAsync(acme, Ct);
        await _presence.ConnectedAsync(globex, Ct);

        await _presence.WatchAsync(acme, Pr1043, Ct);
        await _presence.WatchAsync(globex, Pr1043, Ct);

        _pushes.Viewers.Where(v => v.Tenant == Acme).Last().Viewers.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        _pushes.Viewers.Where(v => v.Tenant == Globex).Last().Viewers.ShouldBe([new Viewer("gina", "Gina Globex")]);
        _pushes.Online.Where(o => o.Tenant == Acme).Last().Online.ShouldBe([new Viewer("maria", "Maria Naidoo")]);
        // The groups the pushes go to carry the tenant of the connection.
        LiveGroups.Viewers(Acme, Pr1043.Entity, Pr1043.Id).ShouldBe($"v:acme:request:{Pr1043.Id}");
    }

    [Fact]
    public async Task A_connection_watches_at_most_the_configured_number_of_entities()
    {
        var presence = new PresenceService(
            _store, _pushes, Options.Create(new PresenceOptions { MaxWatchesPerConnection = 2 }), _clock, NullLogger<PresenceService>.Instance);
        var maria = Conn("c1", "maria", "Maria Naidoo");
        await presence.ConnectedAsync(maria, Ct);

        (await presence.WatchAsync(maria, new PresenceTarget(LiveEntities.Request, "a"), Ct)).ShouldNotBeNull();
        (await presence.WatchAsync(maria, new PresenceTarget(LiveEntities.Request, "b"), Ct)).ShouldNotBeNull();
        (await presence.WatchAsync(maria, new PresenceTarget(LiveEntities.Request, "a"), Ct)).ShouldNotBeNull("watching again is not another one");
        (await presence.WatchAsync(maria, new PresenceTarget(LiveEntities.Request, "c"), Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("request", "11111111-1111-1111-1111-111111111111", true)]
    [InlineData("budget", "CC-100", true)]
    [InlineData("invoice", "a.b_c-1", true)]
    [InlineData("tenant", "acme", false)]
    [InlineData("Request", "1", false)]
    [InlineData("request", "", false)]
    [InlineData("request", "a:b", false)]
    [InlineData("request", "acme:request:1", false)]
    [InlineData("request", "a b", false)]
    [InlineData("request", null, false)]
    [InlineData(null, "1", false)]
    public void Only_known_entities_and_plain_ids_can_be_watched(string? entity, string? id, bool allowed)
    {
        PresenceTarget.TryCreate(entity, id, out _).ShouldBe(allowed);
        PresenceTarget.TryCreate("request", new string('a', 65), out _).ShouldBeFalse("at most 64 characters");
    }

    [Fact]
    public void Viewers_are_distinct_fresh_and_sorted_by_name()
    {
        var now = _clock.GetUtcNow();
        PresenceEntry[] entries =
        [
            new("c1", "maria", "Maria Naidoo", now),
            new("c2", "maria", "Maria Naidoo", now.AddSeconds(-10)),
            new("c3", "ben", "Ben Buyer", now.AddSeconds(-45)),
            new("c4", "old", "Olaf Old", now.AddSeconds(-46)),
        ];

        PresenceView.Viewers(entries, now, TimeSpan.FromSeconds(45))
            .ShouldBe([new Viewer("ben", "Ben Buyer"), new Viewer("maria", "Maria Naidoo")]);
    }

    [Theory]
    [InlineData(15, 45, true)]
    [InlineData(15, 30, true)]
    [InlineData(15, 29, false)]
    [InlineData(20, 30, false)]
    public void Ttl_must_be_at_least_two_heartbeats(int heartbeatSeconds, int ttlSeconds, bool valid)
    {
        var options = new PresenceOptions { HeartbeatInterval = TimeSpan.FromSeconds(heartbeatSeconds), Ttl = TimeSpan.FromSeconds(ttlSeconds) };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true).ShouldBe(valid);
    }

    [Fact]
    public void Defaults_are_the_agreed_values()
    {
        var options = new PresenceOptions();

        options.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(15));
        options.Ttl.ShouldBe(TimeSpan.FromSeconds(45));
        options.SweepInterval.ShouldBe(TimeSpan.FromSeconds(15));
    }

    /// <summary>The data of the Redis store, in memory.</summary>
    private sealed class MemoryPresenceStore : IPresenceStore
    {
        private readonly Dictionary<string, PresenceConnection> _connections = [];
        private readonly Dictionary<string, DateTimeOffset> _lastSeen = [];
        private readonly Dictionary<string, HashSet<PresenceTarget>> _watches = [];

        /// <summary>Runs right after <see cref="StaleAsync"/> read the stale connections (a heartbeat landing in between).</summary>
        public Action? AfterStaleRead { get; set; }

        public bool Known(string connectionId) => _connections.ContainsKey(connectionId);

        public Task<Upserted> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var added = _connections.TryAdd(connection.ConnectionId, connection);
            _lastSeen[connection.ConnectionId] = now;
            return Task.FromResult(new Upserted(added, [.. _watches.GetValueOrDefault(connection.ConnectionId) ?? []]));
        }

        public Task<bool> WatchAsync(
            PresenceConnection connection, PresenceTarget target, DateTimeOffset now, int maxWatches, CancellationToken cancellationToken)
        {
            var watched = _watches.TryGetValue(connection.ConnectionId, out var set) ? set : _watches[connection.ConnectionId] = [];
            if (!watched.Contains(target) && watched.Count >= maxWatches)
            {
                return Task.FromResult(false);
            }

            watched.Add(target);
            _lastSeen[connection.ConnectionId] = now;
            return Task.FromResult(true);
        }

        public Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken)
        {
            _watches.GetValueOrDefault(connection.ConnectionId)?.Remove(target);
            return Task.CompletedTask;
        }

        public Task<RemovedConnection?> RemoveAsync(string connectionId, CancellationToken cancellationToken)
        {
            if (!_connections.Remove(connectionId, out var connection))
            {
                return Task.FromResult<RemovedConnection?>(null);
            }

            _lastSeen.Remove(connectionId);
            _watches.Remove(connectionId, out var targets);
            return Task.FromResult<RemovedConnection?>(new RemovedConnection(connection.TenantId, [.. targets ?? []]));
        }

        // Like the Lua script: only while still stale, and the watches are kept for a later heartbeat.
        public Task<RemovedConnection?> RemoveIfStaleAsync(StaleConnection stale, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            if (_lastSeen.TryGetValue(stale.ConnectionId, out var seen) && seen >= cutoff)
            {
                return Task.FromResult<RemovedConnection?>(null);
            }

            _connections.Remove(stale.ConnectionId);
            _lastSeen.Remove(stale.ConnectionId);
            return Task.FromResult<RemovedConnection?>(
                new RemovedConnection(stale.TenantId, [.. _watches.GetValueOrDefault(stale.ConnectionId) ?? []]));
        }

        public Task<IReadOnlyList<PresenceEntry>> OnlineAsync(string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Entries(c => c.TenantId == tenantId));

        public Task<IReadOnlyList<PresenceEntry>> ViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(Entries(c => c.TenantId == tenantId && _watches.GetValueOrDefault(c.ConnectionId)?.Contains(target) == true));

        public Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            IReadOnlyList<StaleConnection> stale =
            [
                .. _connections.Values.Where(c => _lastSeen[c.ConnectionId] < cutoff).Select(c => new StaleConnection(c.TenantId, c.ConnectionId)),
            ];
            AfterStaleRead?.Invoke();
            return Task.FromResult(stale);
        }

        private IReadOnlyList<PresenceEntry> Entries(Func<PresenceConnection, bool> match) =>
        [
            .. _connections.Values.Where(match).Select(c => new PresenceEntry(c.ConnectionId, c.UserId, c.Name, _lastSeen[c.ConnectionId])),
        ];
    }

    private sealed class RecordingPushes : ILivePushes
    {
        public List<(string Tenant, PresenceTarget Target, IReadOnlyList<Viewer> Viewers)> Viewers { get; } = [];

        public List<(string Tenant, IReadOnlyList<Viewer> Online)> Online { get; } = [];

        public Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken)
        {
            Viewers.Add((tenantId, target, viewers));
            return Task.CompletedTask;
        }

        public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken)
        {
            Online.Add((tenantId, online));
            return Task.CompletedTask;
        }

        public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
