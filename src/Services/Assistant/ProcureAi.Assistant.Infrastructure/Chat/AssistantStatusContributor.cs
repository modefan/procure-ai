using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// The Assistant's part of the status snapshot (plan 06 ruling 10 and spec 6.3 item 6), per tenant, for today (the South African day of
/// <c>Assistant:Quota:TimeZone</c>): <c>questions_today</c> (the users' messages) and <c>guard_blocks_today</c> (answers a guard blocked,
/// <see cref="MessageStatus.Blocked"/>). Counted from the chat history in MongoDB in one aggregation over the conversations changed today (their
/// messages unwound, grouped by tenant, role and status), as the system across every tenant. The quota counters in Redis would need a key scan
/// per user and miss the blocks; the chat history has both.
/// </summary>
internal sealed class AssistantStatusContributor(IMongoDatabase database, IOptions<AssistantOptions> options, TimeProvider time) : IStatusContributor
{
    public const string SectionName = "assistant";

    public string Name => SectionName;

    public async Task<StatusSection> CollectAsync(CancellationToken ct)
    {
        var start = StartOfToday();
        var conversations = database.GetCollection<ConversationDocument>(MongoConversationStore.CollectionName);
        var groups = await conversations.AsQueryable()
            .Where(c => c.UpdatedAt >= start)
            .SelectMany(c => c.Messages!, (c, m) => new { c.TenantId, m.Role, m.Status, m.CreatedAt })
            .Where(m => m.CreatedAt >= start)
            .GroupBy(m => new { m.TenantId, m.Role, m.Status })
            .Select(g => new { g.Key.TenantId, g.Key.Role, g.Key.Status, Count = g.LongCount() })
            .ToListAsync(ct);

        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var name = group.Role == MessageRole.User ? StatusCounters.QuestionsToday
                : group.Status == MessageStatus.Blocked ? StatusCounters.GuardBlocksToday
                : null;
            if (name is not null)
            {
                var key = StatusCounters.Key(group.TenantId, name);
                counters[key] = counters.GetValueOrDefault(key) + group.Count;
            }
        }

        return new StatusSection(SectionName, counters, []);
    }

    /// <summary>Midnight of today in the quota's time zone, as a UTC time (how the chat history stores its times).</summary>
    private DateTime StartOfToday()
    {
        var zone = options.Value.Quota.FindTimeZone() ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var midnight = local.Date;
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).UtcDateTime;
    }
}
