using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcureAi.Notifications.Application.Inbox;
using StackExchange.Redis;

namespace ProcureAi.Notifications.Infrastructure.Inbox;

/// <summary>
/// The bell in Redis, per tenant and user (the tenant is a plain identifier without <c>:</c>, so keys never collide):
/// <c>bell:{tenant}:{user}:index</c> (sorted set, item ID → time), <c>bell:{tenant}:{user}:items</c> (hash, item ID → JSON) and
/// <c>bell:{tenant}:{user}:read</c> (set of read item IDs). Adding is one Lua script: the item is added only if its ID is new (a
/// redelivered message), items past the retention and beyond <see cref="BellOptions.MaxItems"/> are dropped, and all three keys
/// expire after the retention without a new item.
/// </summary>
public sealed class RedisBellStore(IConnectionMultiplexer redis, IOptions<BellOptions> options, TimeProvider time) : IBellStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // KEYS: index, items, read. ARGV: id, score, json, max items, retention cutoff (score), key TTL in seconds. Returns 1 when the item is
    // there afterwards (already there, or added and not trimmed away at once), else 0.
    private const string AddScript = """
        if redis.call('HSETNX', KEYS[2], ARGV[1], ARGV[3]) == 0 then return 1 end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
        local drop = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[5])
        local kept = redis.call('ZCARD', KEYS[1]) - #drop
        local max = tonumber(ARGV[4])
        if kept > max then
          local extra = redis.call('ZRANGE', KEYS[1], #drop, #drop + kept - max - 1)
          for _, x in ipairs(extra) do table.insert(drop, x) end
        end
        for _, x in ipairs(drop) do
          redis.call('ZREM', KEYS[1], x)
          redis.call('HDEL', KEYS[2], x)
          redis.call('SREM', KEYS[3], x)
        end
        for i = 1, 3 do redis.call('EXPIRE', KEYS[i], ARGV[6]) end
        return redis.call('HEXISTS', KEYS[2], ARGV[1])
        """;

    private IDatabase Db => redis.GetDatabase();

    /// <summary>Every bell (all tenants and users): the demo reset deletes them.</summary>
    public const string KeyPattern = "bell:*";

    private static (RedisKey Index, RedisKey Items, RedisKey Read) Keys(string tenantId, string userId)
    {
        var prefix = $"bell:{tenantId}:{userId}";
        return ($"{prefix}:index", $"{prefix}:items", $"{prefix}:read");
    }

    private double Cutoff => (time.GetUtcNow() - options.Value.Retention).ToUnixTimeMilliseconds();

    public async Task<bool> AddAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken)
    {
        var (index, items, read) = Keys(tenantId, userId);
        var stored = new StoredItem(item.Text, item.RequestId, item.Link, item.CreatedAt);
        var added = await Db.ScriptEvaluateAsync(
            AddScript,
            [index, items, read],
            [
                item.Id,
                item.CreatedAt.ToUnixTimeMilliseconds(),
                JsonSerializer.Serialize(stored, Json),
                options.Value.MaxItems,
                Cutoff,
                (long)options.Value.Retention.TotalSeconds,
            ]);
        return (int)added == 1;
    }

    public async Task<BellList> ListAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        var (index, items, read) = Keys(tenantId, userId);
        var db = Db;
        var ids = await db.SortedSetRangeByScoreAsync(
            index, Cutoff, double.PositiveInfinity, order: Order.Descending, take: options.Value.MaxItems);
        if (ids.Length == 0)
        {
            return new BellList([], 0);
        }

        var values = await db.HashGetAsync(items, ids);
        var readFlags = await db.SetContainsAsync(read, ids);
        var list = new List<BellItem>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            if (!values[i].IsNullOrEmpty && JsonSerializer.Deserialize<StoredItem>(values[i].ToString(), Json) is { } stored)
            {
                list.Add(new BellItem(ids[i]!, stored.Text, stored.RequestId, stored.Link, stored.CreatedAt, readFlags[i]));
            }
        }

        return new BellList(list, list.Count(i => !i.Read));
    }

    public async Task<bool> MarkReadAsync(string tenantId, string userId, string itemId, CancellationToken cancellationToken)
    {
        var (_, items, read) = Keys(tenantId, userId);
        var db = Db;
        if (!await db.HashExistsAsync(items, itemId))
        {
            return false;
        }

        await db.SetAddAsync(read, itemId);
        return true;
    }

    public async Task MarkAllReadAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        var (index, _, read) = Keys(tenantId, userId);
        var db = Db;
        var ids = await db.SortedSetRangeByRankAsync(index);
        if (ids.Length > 0)
        {
            await db.SetAddAsync(read, ids);
        }
    }

    private sealed record StoredItem(string Text, Guid? RequestId, string Link, DateTimeOffset CreatedAt);
}
