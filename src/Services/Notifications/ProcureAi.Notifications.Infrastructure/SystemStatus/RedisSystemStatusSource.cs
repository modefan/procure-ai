using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using ProcureAi.Notifications.Application.SystemStatus;
using StackExchange.Redis;

namespace ProcureAi.Notifications.Infrastructure.SystemStatus;

/// <summary>
/// Reads the snapshots every instance writes (<see cref="StatusKeys"/>): the members of the index whose expiry is still ahead, then their
/// values in one <c>MGET</c> (a key that expired between the two reads is skipped). A snapshot that cannot be read is skipped and logged.
/// </summary>
internal sealed class RedisSystemStatusSource(
    IConnectionMultiplexer redis, IDailyStats stats, TimeProvider time, ILogger<RedisSystemStatusSource> logger) : ISystemStatusSource
{
    public async Task<IReadOnlyList<StatusSnapshot>> ReadSnapshotsAsync(CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var keys = await db.SortedSetRangeByScoreAsync(StatusKeys.Index, time.GetUtcNow().ToUnixTimeMilliseconds(), double.PositiveInfinity);
        if (keys.Length == 0)
        {
            return [];
        }

        var values = await db.StringGetAsync([.. keys.Select(k => (RedisKey)k.ToString())]);
        var snapshots = new List<StatusSnapshot>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNullOrEmpty)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<StatusSnapshot>(values[i].ToString(), StatusKeys.Json) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Skipped the unreadable status snapshot {Key}", keys[i].ToString());
            }
        }

        return snapshots;
    }

    public async Task<long> CoalescedTodayAsync(CancellationToken cancellationToken) =>
        (await stats.GetAsync(stats.Today)).GetValueOrDefault(DailyStatNames.Coalesced);
}
