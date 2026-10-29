using System.Globalization;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Chat;
using StackExchange.Redis;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// The daily question quota in Redis (ruling 7): one counter per tenant, user and calendar day of <c>Assistant:Quota:TimeZone</c>
/// (Africa/Johannesburg), key <c>assistant:quota:{tenant}:{user}:{yyyy-MM-dd}</c>, kept two days. A Lua script checks and counts in one
/// step, so two questions at once never both take the last one, and a refused question is not counted. The clock is the injected
/// <see cref="TimeProvider"/>.
/// </summary>
internal sealed class RedisQuestionQuota(IConnectionMultiplexer redis, IOptions<AssistantOptions> options, TimeProvider time) : IQuestionQuota
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(2);

    // KEYS[1] the counter, ARGV[1] the limit, ARGV[2] the lifetime in seconds. Returns the count, negated when the limit was reached.
    private const string CountScript = """
        local used = tonumber(redis.call('GET', KEYS[1]) or '0')
        if used >= tonumber(ARGV[1]) then
          return -used
        end
        used = redis.call('INCR', KEYS[1])
        redis.call('EXPIRE', KEYS[1], ARGV[2])
        return used
        """;

    public async Task<QuotaCount> TryCountAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        var (key, limit, resetsAt) = Today(tenantId, userId);
        var result = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            CountScript, [key], [limit, (long)KeyLifetime.TotalSeconds]);
        return result > 0
            ? new QuotaCount(true, (int)result, limit, resetsAt)
            : new QuotaCount(false, (int)-result, limit, resetsAt);
    }

    public async Task<QuotaCount> GetAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        var (key, limit, resetsAt) = Today(tenantId, userId);
        var value = await redis.GetDatabase().StringGetAsync(key);
        var used = value.TryParse(out long count) ? (int)count : 0;
        return new QuotaCount(used < limit, used, limit, resetsAt);
    }

    private (RedisKey Key, int Limit, DateTimeOffset ResetsAt) Today(string tenantId, string userId)
    {
        var quota = options.Value.Quota;
        var zone = quota.FindTimeZone() ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var day = local.Date;
        var midnight = day.AddDays(1);
        var resetsAt = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
        var key = $"assistant:quota:{tenantId}:{userId}:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        return (key, quota.QuestionsPerDay, resetsAt);
    }
}
