namespace ProcureAi.BuildingBlocks.Application.Caching;

/// <summary>Drops cached GET responses by tag after a write, so the next read cannot return the old state.</summary>
public interface ICacheInvalidator
{
    /// <summary>Evicts now. For callers that already know the write has committed (an HTTP controller after its command returned).</summary>
    Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// For message handlers: evicts once the handler's transaction has committed (nothing is evicted when the handler fails
    /// and rolls back). A failure or timeout of the eviction is logged, never raised: the cached entries expire on their own.
    /// Outside a message handler there is no transaction to wait for and it evicts immediately.
    /// </summary>
    Task RemoveAfterCommitAsync(IEnumerable<string> tags);
}
