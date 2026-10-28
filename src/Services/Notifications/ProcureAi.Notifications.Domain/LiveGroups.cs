namespace ProcureAi.Notifications.Domain;

/// <summary>
/// The SignalR groups of the live hub. A connection joins the groups of the tenant and user its token (or, with the admin key, its
/// <c>X-Tenant-Id</c> header) names; a client can never name a group itself, so no client receives another tenant's pushes.
/// </summary>
public static class LiveGroups
{
    /// <summary>Every connection of the tenant: <c>t:{tenantId}</c>. Live updates (<c>EntityChanged</c>) go here.</summary>
    public static string Tenant(string tenantId) => $"t:{Required(tenantId, nameof(tenantId))}";

    /// <summary>Every connection of one user of the tenant: <c>u:{tenantId}:{userId}</c> (bell items, plan 04 Task 1b).</summary>
    public static string User(string tenantId, string userId) =>
        $"u:{Required(tenantId, nameof(tenantId))}:{Required(userId, nameof(userId))}";

    /// <summary>
    /// Every connection viewing one entity of the tenant: <c>v:{tenantId}:{entity}:{id}</c> (presence, <c>ViewersChanged</c>). The tenant
    /// comes from the caller's token, never from the client; entity and ID are checked first (no <c>:</c>), so a name is unambiguous.
    /// </summary>
    public static string Viewers(string tenantId, string entity, string id) =>
        $"v:{Required(tenantId, nameof(tenantId))}:{Required(entity, nameof(entity))}:{Required(id, nameof(id))}";

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A group needs a value.", name) : value;
}
