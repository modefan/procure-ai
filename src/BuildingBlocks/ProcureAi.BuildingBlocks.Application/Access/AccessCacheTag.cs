namespace ProcureAi.BuildingBlocks.Application.Access;

/// <summary>The cache tag every cached Access answer of a tenant carries, and the one <c>AccessChanged</c> evicts.</summary>
public static class AccessCacheTag
{
    public static string Tenant(string tenantId) => $"access:{tenantId}";
}
