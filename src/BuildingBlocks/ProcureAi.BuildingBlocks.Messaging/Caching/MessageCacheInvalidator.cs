using ProcureAi.BuildingBlocks.Web.Caching;
using Wolverine;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Messaging.Caching;

/// <summary>
/// The invalidator of a service with messaging. <see cref="RemoveAfterCommitAsync"/> publishes <see cref="EvictCacheTags"/> on the
/// handler's own message context, so Wolverine's outbox holds it back until the handler's EF Core transaction commits and
/// discards it on rollback. Evicting before the commit would let a concurrent read cache the old row again.
/// </summary>
/// <param name="bus">Wolverine builds the handler's dependencies in the generated handler code, so this is the handler's own context.</param>
public sealed class MessageCacheInvalidator(IFusionCache cache, IMessageBus bus) : FusionCacheInvalidator(cache)
{
    public override Task RemoveAfterCommitAsync(IEnumerable<string> tags) =>
        bus.PublishAsync(new EvictCacheTags([.. tags])).AsTask();
}
