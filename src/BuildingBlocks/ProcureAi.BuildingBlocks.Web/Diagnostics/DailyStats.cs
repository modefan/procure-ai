using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ProcureAi.BuildingBlocks.Web.Diagnostics;

/// <summary>Small per-day counters for the system status screen (for example how many duplicate requests were coalesced).</summary>
public interface IDailyStats
{
    /// <summary>Adds one to today's counter. Never throws: a Redis failure is logged at Debug and the request carries on.</summary>
    Task IncrementAsync(string name);

    /// <summary>All counters of that day; empty when there are none or Redis is not reachable.</summary>
    Task<IReadOnlyDictionary<string, long>> GetAsync(DateOnly day);

    /// <summary>The day the counters count now (South Africa).</summary>
    DateOnly Today { get; }
}

public static class DailyStatNames
{
    /// <summary>Identical requests that waited for another's result, and idempotent replays.</summary>
    public const string Coalesced = "coalesced";
}

public static class DailyStatsExtensions
{
    /// <summary>Counts a coalesced request; whatever the store does, the request is never affected.</summary>
    public static async Task TryCountCoalescedAsync(this IDailyStats stats)
    {
        try
        {
            await stats.IncrementAsync(DailyStatNames.Coalesced);
        }
        catch (Exception)
        {
            // A statistic must never fail a request.
        }
    }
}

/// <summary>Redis hash <c>procure:stats:{yyyy-MM-dd}</c> (day in South Africa), kept 3 days.</summary>
public sealed class RedisDailyStats(IConnectionMultiplexer redis, ILogger<RedisDailyStats> logger, TimeProvider? time = null) : IDailyStats
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private static readonly TimeZoneInfo Zone = FindZone();

    public static string KeyFor(DateOnly day) => "procure:stats:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Today in South Africa.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((time ?? TimeProvider.System).GetUtcNow(), Zone).DateTime);

    public Task IncrementAsync(string name)
    {
        try
        {
            var db = redis.GetDatabase();
            var key = KeyFor(Today);
            // Fire and forget: the request never waits for, or fails on, a statistic. Commands on one connection stay in order.
            _ = db.HashIncrementAsync(key, name, 1, CommandFlags.FireAndForget);
            _ = db.KeyExpireAsync(key, Retention, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not count {Stat}", name);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetAsync(DateOnly day)
    {
        try
        {
            var entries = await redis.GetDatabase().HashGetAllAsync(KeyFor(day));
            return entries.ToDictionary(e => (string)e.Name!, e => (long)e.Value);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the stats of {Day}", day);
            return new Dictionary<string, long>();
        }
    }

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // South Africa has no daylight saving time, so a fixed offset is exact.
            return TimeZoneInfo.CreateCustomTimeZone("SAST", TimeSpan.FromHours(2), "SAST", "SAST");
        }
    }
}

/// <summary>Used by hosts without Redis; counts nothing.</summary>
public sealed class NoOpDailyStats : IDailyStats
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public Task IncrementAsync(string name) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, long>> GetAsync(DateOnly day) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
}
