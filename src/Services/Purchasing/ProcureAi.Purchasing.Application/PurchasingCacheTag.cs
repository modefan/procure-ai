using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Purchasing.Application;

/// <summary>
/// The tags of the cached order reads: one order, and the tenant list. The API reads use the templates in <c>[CachedGet]</c>;
/// controllers after a write and handlers after their commit evict the formatted tags.
/// </summary>
public static class PurchasingCacheTag
{
    /// <summary>The route value is <c>id</c>.</summary>
    public const string ItemTemplate = "orders:{tenant}:{id}";

    public const string ListTemplate = "orders:{tenant}:list";

    /// <summary>Both tags a change of one order touches.</summary>
    public static string[] Of(string? tenantId, Guid orderId) =>
    [
        CacheTag.Format(ItemTemplate, CacheTag.Tenant(tenantId), ("id", orderId))!,
        CacheTag.Format(ListTemplate, CacheTag.Tenant(tenantId))!,
    ];
}
