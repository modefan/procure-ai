using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>A conversation without its messages, for lists.</summary>
/// <summary>A conversation without its messages. The counts are filled only where the list asks for them (the admin list).</summary>
public sealed record ConversationSummary(
    Guid Id, string UserId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount = 0, int BlockedCount = 0);

/// <summary>
/// The conversations of the caller's tenant (MongoDB <c>assistantchatdb</c>, collection <c>conversations</c>). Every call is limited to the
/// caller's tenant; the owner methods also to <c>ownerId</c>, so another user's conversation is simply not found.
/// </summary>
public interface IConversationStore
{
    Task InsertAsync(Conversation conversation, CancellationToken cancellationToken);

    /// <summary>The owner's conversation, or null (also when it is another user's or another tenant's).</summary>
    Task<Conversation?> FindAsync(Guid id, string ownerId, CancellationToken cancellationToken);

    /// <summary>The owner's newest conversations, by last change.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(string ownerId, int limit, CancellationToken cancellationToken);

    /// <summary>Stores the conversation as it is now; false when it no longer exists (deleted meanwhile).</summary>
    Task<bool> SaveAsync(Conversation conversation, CancellationToken cancellationToken);

    /// <summary>Deletes the owner's conversation; false when there was none.</summary>
    Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken cancellationToken);

    /// <summary>Admin: any conversation of the tenant, or null.</summary>
    Task<Conversation?> FindInTenantAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Admin: one page of the tenant's conversations (of one user when given), newest change first, and the total.</summary>
    Task<(IReadOnlyList<ConversationSummary> Items, int Total)> ListInTenantAsync(
        string? userId, int skip, int take, CancellationToken cancellationToken);
}
