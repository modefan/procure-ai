using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Budgets.Application;

/// <summary>
/// The tag every cached budget read of a tenant carries. The API reads use <see cref="Template"/> in <c>[CachedGet]</c>; the
/// handlers and the API write path evict <see cref="Tenant"/>, so both come from one definition.
/// </summary>
public static class BudgetCacheTag
{
    public const string Template = "budgets:{tenant}";

    public static string Tenant(string? tenantId) => CacheTag.Format(Template, CacheTag.Tenant(tenantId))!;
}
