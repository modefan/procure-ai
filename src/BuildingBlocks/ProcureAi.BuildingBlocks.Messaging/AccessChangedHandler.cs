using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts.Access;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// Access changed a matrix or the organisation of a tenant: drop everything cached about that tenant from Access (the
/// permissions and approvers <c>AccessClient</c> holds). The eviction reaches the other instances through the cache backplane.
/// Every service except Access listens (see <see cref="MessagingSetup"/>); Access itself caches nothing from Access.
/// </summary>
public static class AccessChangedHandler
{
    public static Task Handle(AccessChanged message, ICacheInvalidator cache) =>
        cache.RemoveAfterCommitAsync([AccessCacheTag.Tenant(message.TenantId)]);
}
