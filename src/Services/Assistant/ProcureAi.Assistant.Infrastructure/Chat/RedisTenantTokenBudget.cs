using System.Globalization;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using StackExchange.Redis;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// The daily token cap per tenant in Redis (<c>AI:DailyTokenCapPerTenant</c>): one counter per tenant and calendar day of
/// <c>Assistant:Quota:TimeZone</c>, key <c>assistant:tokens:{tenant}:{yyyy-MM-dd}</c>, kept two days, added to with <c>INCRBY</c> (atomic
/// across instances). The clock is the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class RedisTenantTokenBudget(
    IConnectionMultiplexer redis, IOptions<AssistantOptions> options, IOptions<AiOptions> ai, TimeProvider time) : ITenantTokenBudget
{
    /// <summary>Every counter (all tenants and days). The demo reset keeps them on purpose: the cap is a cost guard.</summary>
    public const string KeyPattern = "assistant:tokens:*";

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(2);

    public async Task<TokenBudget> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        var (key, resetsAt) = Today(tenantId);
        var value = await redis.GetDatabase().StringGetAsync(key);
        return new TokenBudget(value.TryParse(out long used) ? used : 0, ai.Value.DailyTokenCapPerTenant, resetsAt);
    }

    public async Task AddAsync(string tenantId, long tokens, CancellationToken cancellationToken)
    {
        if (tokens <= 0)
        {
            return;
        }

        var (key, _) = Today(tenantId);
        var database = redis.GetDatabase();
        await database.StringIncrementAsync(key, tokens);
        await database.KeyExpireAsync(key, KeyLifetime);
    }

    private (RedisKey Key, DateTimeOffset ResetsAt) Today(string tenantId)
    {
        var zone = options.Value.Quota.FindTimeZone() ?? TimeZoneInfo.Utc;
        var day = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Date;
        var midnight = day.AddDays(1);
        var resetsAt = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
        return ($"assistant:tokens:{tenantId}:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", resetsAt);
    }
}
