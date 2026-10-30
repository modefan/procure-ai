using MongoDB.Driver;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// The conversations in MongoDB. Every call goes through <see cref="TenantCollection{T}"/>, so it only ever sees the caller's tenant; the
/// owner methods add <c>UserId == owner</c> to the filter, so another user's conversation is not found either. Lists leave the messages out.
/// </summary>
internal sealed class MongoConversationStore(TenantCollection<ConversationDocument> conversations) : IConversationStore
{
    public const string CollectionName = "conversations";

    private static readonly ProjectionDefinition<ConversationDocument, ConversationDocument> WithoutMessages =
        Builders<ConversationDocument>.Projection.Exclude(c => c.Messages);

    /// <summary>The admin list: the messages are cut down to their status field, enough to count them and the blocked ones.</summary>
    private static readonly ProjectionDefinition<ConversationDocument, ConversationDocument> WithMessageStatusOnly =
        Builders<ConversationDocument>.Projection
            .Include(c => c.TenantId).Include(c => c.UserId).Include(c => c.Title).Include(c => c.CreatedAt).Include(c => c.UpdatedAt)
            .Include("Messages.Status");

    public Task InsertAsync(Conversation conversation, CancellationToken cancellationToken) =>
        conversations.InsertOneAsync(ConversationDocument.From(conversation), cancellationToken);

    public async Task<Conversation?> FindAsync(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        var key = Key(id);
        var found = await conversations.Find(c => c.Id == key && c.UserId == ownerId).FirstOrDefaultAsync(cancellationToken);
        return found?.ToDomain();
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(string ownerId, int limit, CancellationToken cancellationToken)
    {
        var found = await conversations.Find(c => c.UserId == ownerId)
            .Project(WithoutMessages).SortByDescending(c => c.UpdatedAt).Limit(limit).ToListAsync(cancellationToken);
        return [.. found.Select(Summary)];
    }

    public async Task<bool> SaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        var key = Key(conversation.Id);
        var owner = conversation.UserId;
        var result = await conversations.ReplaceOneAsync(
            c => c.Id == key && c.UserId == owner, ConversationDocument.From(conversation), cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        var key = Key(id);
        var result = await conversations.DeleteOneAsync(c => c.Id == key && c.UserId == ownerId, cancellationToken);
        return result.DeletedCount == 1;
    }

    public async Task<Conversation?> FindInTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = Key(id);
        var found = await conversations.Find(c => c.Id == key).FirstOrDefaultAsync(cancellationToken);
        return found?.ToDomain();
    }

    public async Task<(IReadOnlyList<ConversationSummary> Items, int Total)> ListInTenantAsync(
        string? userId, int skip, int take, CancellationToken cancellationToken)
    {
        var filter = userId is null
            ? Builders<ConversationDocument>.Filter.Empty
            : Builders<ConversationDocument>.Filter.Eq(c => c.UserId, userId);
        var total = await conversations.CountAsync(filter, cancellationToken);
        var page = await conversations.Find(filter)
            .Project(WithMessageStatusOnly).SortByDescending(c => c.UpdatedAt).Skip(skip).Limit(take).ToListAsync(cancellationToken);
        return ([.. page.Select(c => Summary(c) with { MessageCount = c.Messages?.Count ?? 0, BlockedCount = c.Messages?.Count(m => m.Status == MessageStatus.Blocked) ?? 0 })], (int)total);
    }

    private static string Key(Guid id) => id.ToString("D");

    private static ConversationSummary Summary(ConversationDocument c) =>
        new(Guid.Parse(c.Id), c.UserId, c.Title, ConversationDocument.Utc(c.CreatedAt), ConversationDocument.Utc(c.UpdatedAt));
}

/// <summary>
/// The index <c>(TenantId, UserId, UpdatedAt desc)</c>: a user's newest conversations, and the admin's by user. Creating an index that
/// exists is a no-op.
/// </summary>
internal sealed class ConversationIndexMigration : IMongoMigration
{
    public const string IndexName = "tenant_user_updated";

    public async Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<ConversationDocument>(MongoConversationStore.CollectionName);
        var keys = Builders<ConversationDocument>.IndexKeys.Ascending(c => c.TenantId).Ascending(c => c.UserId).Descending(c => c.UpdatedAt);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<ConversationDocument>(keys, new CreateIndexOptions { Name = IndexName }), cancellationToken: cancellationToken);
    }
}
