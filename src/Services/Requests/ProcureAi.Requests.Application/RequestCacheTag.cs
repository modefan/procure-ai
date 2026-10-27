using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Requests.Application;

/// <summary>
/// The tags of the cached request reads: one request, and the tenant's list. The API reads use the templates in
/// <c>[CachedGet]</c>; the controller after a write and the outcome handlers after their commit evict the formatted tags, so
/// both come from one definition.
/// </summary>
public static class RequestCacheTag
{
    /// <summary>The route value is <c>id</c>.</summary>
    public const string ItemTemplate = "requests:{tenant}:{id}";

    public const string ListTemplate = "requests:{tenant}:list";

    /// <summary>Both tags a change of one request touches.</summary>
    public static string[] Of(string? tenantId, Guid requestId) =>
    [
        CacheTag.Format(ItemTemplate, CacheTag.Tenant(tenantId), ("id", requestId))!,
        CacheTag.Format(ListTemplate, CacheTag.Tenant(tenantId))!,
    ];
}
