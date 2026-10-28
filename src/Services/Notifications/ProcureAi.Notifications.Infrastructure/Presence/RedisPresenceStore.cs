using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcureAi.Notifications.Application.Presence;
using StackExchange.Redis;

namespace ProcureAi.Notifications.Infrastructure.Presence;

/// <summary>
/// Presence in Redis, shared by every instance:
/// <list type="bullet">
/// <item><c>presence:{tenant}:online</c> and <c>presence:{tenant}:view:{entity}:{id}</c>: sorted sets, member the connection ID, score the
/// last heartbeat (Unix milliseconds of the presence clock).</item>
/// <item><c>presence:connections</c>: hash, connection ID → tenant, user ID and name.</item>
/// <item><c>presence:conn:{connectionId}:views</c>: set of the targets (<c>{entity}:{id}</c>) the connection watches.</item>
/// <item><c>presence:tenants</c>: set of the tenants with connections, for the sweeper.</item>
/// </list>
/// Tenants, entities and IDs are plain identifiers (no <c>:</c>), so keys are unambiguous. Readers skip entries older than the TTL even
/// before the sweeper removed them; view keys and the per-connection sets also expire after two TTLs without a write, so nothing is left
/// behind if a removal was interrupted.
/// </summary>
public sealed class RedisPresenceStore(IConnectionMultiplexer redis, IOptions<PresenceOptions> options) : IPresenceStore
{
    private const string Connections = "presence:connections";
    private const string Tenants = "presence:tenants";

    private IDatabase Db => redis.GetDatabase();

    private TimeSpan KeyTtl => 2 * options.Value.Ttl;

    private static string Online(string tenantId) => $"presence:{tenantId}:online";

    private static string View(string tenantId, PresenceTarget target) => $"presence:{tenantId}:view:{target.Entity}:{target.Id}";

    private static string ConnectionViews(string connectionId) => $"presence:conn:{connectionId}:views";

    private static double Score(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    public async Task<bool> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var db = Db;
        var views = await db.SetMembersAsync(ConnectionViews(connection.ConnectionId));
        var batch = db.CreateBatch();
        var info = batch.HashSetAsync(Connections, connection.ConnectionId, Serialize(connection));
        var added = batch.SortedSetAddAsync(Online(connection.TenantId), connection.ConnectionId, Score(now));
        var tenant = batch.SetAddAsync(Tenants, connection.TenantId);
        var refreshed = new List<Task>();
        foreach (var key in views)
        {
            if (PresenceTarget.FromKey(key!) is { } target)
            {
                var view = View(connection.TenantId, target);
                refreshed.Add(batch.SortedSetAddAsync(view, connection.ConnectionId, Score(now), SortedSetWhen.Exists));
                refreshed.Add(batch.KeyExpireAsync(view, KeyTtl));
            }
        }

        refreshed.Add(batch.KeyExpireAsync(ConnectionViews(connection.ConnectionId), KeyTtl));
        batch.Execute();
        await Task.WhenAll([info, tenant, .. refreshed]);
        return await added;
    }

    public async Task<bool> WatchAsync(
        PresenceConnection connection, PresenceTarget target, DateTimeOffset now, int maxWatches, CancellationToken cancellationToken)
    {
        var db = Db;
        var views = ConnectionViews(connection.ConnectionId);
        if (!await db.SetContainsAsync(views, target.Key) && await db.SetLengthAsync(views) >= maxWatches)
        {
            return false;
        }

        var view = View(connection.TenantId, target);
        var batch = db.CreateBatch();
        Task[] writes =
        [
            batch.HashSetAsync(Connections, connection.ConnectionId, Serialize(connection)),
            batch.SetAddAsync(views, target.Key),
            batch.KeyExpireAsync(views, KeyTtl),
            batch.SortedSetAddAsync(view, connection.ConnectionId, Score(now)),
            batch.KeyExpireAsync(view, KeyTtl),
        ];
        batch.Execute();
        await Task.WhenAll(writes);
        return true;
    }

    public async Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken)
    {
        var batch = Db.CreateBatch();
        Task[] writes =
        [
            batch.SetRemoveAsync(ConnectionViews(connection.ConnectionId), target.Key),
            batch.SortedSetRemoveAsync(View(connection.TenantId, target), connection.ConnectionId),
        ];
        batch.Execute();
        await Task.WhenAll(writes);
    }

    public async Task<RemovedConnection?> RemoveAsync(string connectionId, string? tenantHint, CancellationToken cancellationToken)
    {
        var db = Db;
        var info = Deserialize(await db.HashGetAsync(Connections, connectionId));
        var tenantId = info?.TenantId ?? tenantHint;
        if (tenantId is null)
        {
            return null;
        }

        var targets = (await db.SetMembersAsync(ConnectionViews(connectionId)))
            .Select(key => PresenceTarget.FromKey(key!))
            .OfType<PresenceTarget>()
            .ToList();
        var batch = db.CreateBatch();
        List<Task> writes =
        [
            batch.SortedSetRemoveAsync(Online(tenantId), connectionId),
            batch.HashDeleteAsync(Connections, connectionId),
            batch.KeyDeleteAsync(ConnectionViews(connectionId)),
            .. targets.Select(target => batch.SortedSetRemoveAsync(View(tenantId, target), connectionId)),
        ];
        batch.Execute();
        await Task.WhenAll(writes);
        return new RemovedConnection(tenantId, targets);
    }

    public Task<IReadOnlyList<PresenceEntry>> OnlineAsync(string tenantId, CancellationToken cancellationToken) =>
        EntriesAsync(Online(tenantId));

    public Task<IReadOnlyList<PresenceEntry>> ViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken) =>
        EntriesAsync(View(tenantId, target));

    public async Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var db = Db;
        var stale = new List<StaleConnection>();
        foreach (var tenant in await db.SetMembersAsync(Tenants))
        {
            var tenantId = tenant.ToString();
            var online = Online(tenantId);
            // Strictly before the cutoff: a heartbeat exactly TTL ago still counts (the same rule as PresenceView).
            var old = await db.SortedSetRangeByScoreAsync(online, double.NegativeInfinity, Score(cutoff), Exclude.Stop);
            stale.AddRange(old.Select(connectionId => new StaleConnection(tenantId, connectionId!)));
            if (old.Length == 0 && await db.SortedSetLengthAsync(online) == 0)
            {
                // A heartbeat of the tenant adds it again.
                await db.SetRemoveAsync(Tenants, tenantId);
            }
        }

        return stale;
    }

    private async Task<IReadOnlyList<PresenceEntry>> EntriesAsync(string key)
    {
        var db = Db;
        var members = await db.SortedSetRangeByRankWithScoresAsync(key);
        if (members.Length == 0)
        {
            return [];
        }

        var infos = await db.HashGetAsync(Connections, [.. members.Select(m => m.Element)]);
        var entries = new List<PresenceEntry>(members.Length);
        for (var i = 0; i < members.Length; i++)
        {
            // A member whose details are gone was being removed: not a viewer.
            if (Deserialize(infos[i]) is { } info)
            {
                entries.Add(new PresenceEntry(
                    members[i].Element!, info.UserId, info.Name, DateTimeOffset.FromUnixTimeMilliseconds((long)members[i].Score)));
            }
        }

        return entries;
    }

    private sealed record ConnectionInfo(string TenantId, string UserId, string Name);

    private static string Serialize(PresenceConnection connection) =>
        JsonSerializer.Serialize(new ConnectionInfo(connection.TenantId, connection.UserId, connection.Name));

    private static ConnectionInfo? Deserialize(RedisValue value) =>
        value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ConnectionInfo>(value.ToString());
}
