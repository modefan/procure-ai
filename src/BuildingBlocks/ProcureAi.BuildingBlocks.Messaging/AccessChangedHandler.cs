using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.Contracts.Access;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// Access changed a matrix or the organisation of a tenant: drop everything cached about that tenant from Access (the
/// permissions and approvers <c>AccessClient</c> holds). The tenant's cache generation advances first, which makes the old
/// entries unreachable on every instance, so fail-safe cannot serve a revoked permission while Access is down. The tag eviction
/// after it only frees them early. Every service except Access listens (see <see cref="MessagingSetup"/>).
/// </summary>
public static class AccessChangedHandler
{
    public static async Task Handle(AccessChanged message, AccessCacheGeneration generation, ICacheInvalidator cache)
    {
        await generation.AdvanceAsync(message.TenantId);
        await cache.RemoveAfterCommitAsync([AccessCacheTag.Tenant(message.TenantId)]);
    }
}
