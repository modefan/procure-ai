using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>
/// The tags of the cached conversation reads: one conversation (the owner's and the admin's read), the tenant's lists, and the tenant's
/// quota reads. Every change of a conversation drops all three (the lists are per user in the cache key, the tag is per tenant).
/// </summary>
public static class ConversationCacheTag
{
    /// <summary>The route value is <c>id</c>.</summary>
    public const string ConversationTemplate = "assistant:{tenant}:conversations:{id}";

    public const string ListTemplate = "assistant:{tenant}:conversations";

    public const string QuotaTemplate = "assistant:{tenant}:quota";

    public static string[] Of(string? tenantId, Guid conversationId) =>
    [
        CacheTag.Format(ConversationTemplate, CacheTag.Tenant(tenantId), ("id", conversationId))!,
        CacheTag.Format(ListTemplate, CacheTag.Tenant(tenantId))!,
        CacheTag.Format(QuotaTemplate, CacheTag.Tenant(tenantId))!,
    ];
}
