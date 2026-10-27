using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Approvals.Application;

/// <summary>
/// The tags of the cached approval reads: the inbox of a tenant (every approver's) and the chain of one request. The API reads
/// use the templates in <c>[CachedGet]</c>; the saga and the API write path evict the formatted tags, so both come from one
/// definition.
/// </summary>
public static class ApprovalCacheTag
{
    public const string InboxTemplate = "approvals:{tenant}:inbox";

    /// <summary>The route value is <c>requestId</c>.</summary>
    public const string ChainTemplate = "approvals:{tenant}:{requestId}";

    public static string Inbox(string? tenantId) => CacheTag.Format(InboxTemplate, CacheTag.Tenant(tenantId))!;

    public static string Chain(string? tenantId, Guid requestId) =>
        CacheTag.Format(ChainTemplate, CacheTag.Tenant(tenantId), ("requestId", requestId))!;

    /// <summary>Both tags a change of a request's approval touches.</summary>
    public static string[] Of(string? tenantId, Guid requestId) => [Inbox(tenantId), Chain(tenantId, requestId)];
}
