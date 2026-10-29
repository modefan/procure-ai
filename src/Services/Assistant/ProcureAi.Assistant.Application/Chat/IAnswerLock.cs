namespace ProcureAi.Assistant.Application.Chat;

/// <summary>
/// One answer at a time per conversation, across every instance (ruling 6): a Redis lock <c>assistant:answer:{tenant}:{conversationId}</c>.
/// It is held with a lease that the holder keeps extending, so a process that dies leaves no stale lock: the lease runs out
/// (<c>Assistant:Answer:LockLease</c>).
/// </summary>
public interface IAnswerLock
{
    /// <summary>The held lock, or null when another answer of the conversation is being written. Disposing it releases it (more than once is safe).</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string tenantId, Guid conversationId, CancellationToken cancellationToken);
}
