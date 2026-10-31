using StackExchange.Redis;

namespace ProcureAi.BuildingBlocks.Web.Demo;

/// <summary>The Redis half of a demo reset: state a service keeps in plain Redis keys (not FusionCache), e.g. bell items or quotas.</summary>
public static class RedisDemoReset
{
    /// <summary>Deletes every key matching <paramref name="pattern"/> (a Redis glob, e.g. <c>bell:*</c>) on every primary, by SCAN. Returns how many.</summary>
    public static async Task<long> DeleteKeysAsync(IConnectionMultiplexer redis, string pattern)
    {
        long deleted = 0;
        var database = redis.GetDatabase();
        foreach (var server in redis.GetServers().Where(s => s.IsConnected && !s.IsReplica))
        {
            var batch = new List<RedisKey>(256);
            await foreach (var key in server.KeysAsync(database.Database, pattern, pageSize: 256))
            {
                batch.Add(key);
                if (batch.Count == 256)
                {
                    deleted += await database.KeyDeleteAsync([.. batch]);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                deleted += await database.KeyDeleteAsync([.. batch]);
            }
        }

        return deleted;
    }
}
