namespace ProcureAi.BuildingBlocks.Application.Access;

/// <summary>The cache tag every cached Access answer of a tenant carries, and the one <c>AccessChanged</c> evicts.</summary>
public static class AccessCacheTag
{
    /// <summary>The same tag as a <c>[CachedGet]</c> template, for a read that depends on what Access says (evicted with it).</summary>
    public const string Template = "access:{tenant}";

    public static string Tenant(string tenantId) => $"access:{tenantId}";
}
