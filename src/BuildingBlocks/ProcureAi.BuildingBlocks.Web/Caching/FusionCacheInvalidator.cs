using ProcureAi.BuildingBlocks.Application.Caching;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Evicts tags from FusionCache (memory, Redis and the backplane to the other instances). Services with messaging
/// replace it by a subclass that defers <see cref="RemoveAfterCommitAsync"/> until the handler's transaction commits.
/// </summary>
public class FusionCacheInvalidator(IFusionCache cache) : ICacheInvalidator
{
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        foreach (var tag in tags)
        {
            await cache.RemoveByTagAsync(tag, token: cancellationToken);
        }
    }

    /// <inheritdoc />
    public virtual Task RemoveAfterCommitAsync(IEnumerable<string> tags) => RemoveByTagAsync(tags);
}
