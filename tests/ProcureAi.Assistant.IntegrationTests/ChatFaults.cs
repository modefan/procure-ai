using System.Collections.Concurrent;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>Faults the conversation store and the answer lock show for the tenants a test lists (all other tenants are untouched).</summary>
public sealed class ChatFaults
{
    /// <summary>The lock takes this long to release (so a release that happens after <c>done</c> shows as a 409).</summary>
    public static readonly TimeSpan SlowRelease = TimeSpan.FromMilliseconds(700);

    public ConcurrentDictionary<string, bool> SlowLockRelease { get; } = new(StringComparer.Ordinal);

    /// <summary>Saving a conversation whose last message is an answer throws (the store is down).</summary>
    public ConcurrentDictionary<string, bool> FailingAnswerSave { get; } = new(StringComparer.Ordinal);

    /// <summary>Every save finds no conversation (it was deleted just before).</summary>
    public ConcurrentDictionary<string, bool> Vanishing { get; } = new(StringComparer.Ordinal);
}

internal sealed class FaultyAnswerLock(IAnswerLock inner, ChatFaults faults) : IAnswerLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var held = await inner.TryAcquireAsync(tenantId, conversationId, cancellationToken);
        return held is null || !faults.SlowLockRelease.ContainsKey(tenantId) ? held : new SlowRelease(held);
    }

    private sealed class SlowRelease(IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                await Task.Delay(ChatFaults.SlowRelease);
                await inner.DisposeAsync();
            }
        }
    }
}

internal sealed class FaultyConversationStore(IConversationStore inner, ChatFaults faults) : IConversationStore
{
    public Task InsertAsync(Conversation conversation, CancellationToken cancellationToken) => inner.InsertAsync(conversation, cancellationToken);

    public Task<Conversation?> FindAsync(Guid id, string ownerId, CancellationToken cancellationToken) => inner.FindAsync(id, ownerId, cancellationToken);

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(string ownerId, int limit, CancellationToken cancellationToken) =>
        inner.ListAsync(ownerId, limit, cancellationToken);

    public Task<bool> SaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        if (faults.Vanishing.ContainsKey(conversation.TenantId))
        {
            return Task.FromResult(false);
        }

        if (faults.FailingAnswerSave.ContainsKey(conversation.TenantId) && conversation.Messages[^1].Role == MessageRole.Assistant)
        {
            throw new InvalidOperationException("The conversation store is down (test).");
        }

        return inner.SaveAsync(conversation, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken cancellationToken) => inner.DeleteAsync(id, ownerId, cancellationToken);

    public Task<Conversation?> FindInTenantAsync(Guid id, CancellationToken cancellationToken) => inner.FindInTenantAsync(id, cancellationToken);

    public Task<(IReadOnlyList<ConversationSummary> Items, int Total)> ListInTenantAsync(
        string? userId, int skip, int take, CancellationToken cancellationToken) =>
        inner.ListInTenantAsync(userId, skip, take, cancellationToken);
}
