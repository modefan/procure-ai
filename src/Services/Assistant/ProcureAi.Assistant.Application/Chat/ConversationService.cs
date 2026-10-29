using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>A conversation in the caller's list.</summary>
public sealed record ConversationListItem(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A conversation in the admin list: whose it is, too.</summary>
public sealed record AdminConversationListItem(Guid Id, string UserId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreatedConversation(Guid Id);

public sealed record SourceDto(string Ref, Guid ChunkId, SourceType Type, string Title, IReadOnlyList<string> SectionPath, string? Reference);

public sealed record ToolStepDto(string Id, string Name, string Arguments, bool Ok, string Summary, long DurationMs);

public sealed record UsageDto(long Input, long Output);

/// <summary>A message as its owner sees it: text, status, sources, tool steps, usage and model.</summary>
public sealed record MessageDto(
    Guid Id, MessageRole Role, string Text, MessageStatus Status, DateTimeOffset CreatedAt, IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<ToolStepDto> ToolSteps, UsageDto Usage, string? Model);

public sealed record ConversationDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<MessageDto> Messages);

/// <summary>A message with everything stored about it (ruling 18): also the guard flags and the timings.</summary>
public sealed record AdminMessageDto(
    Guid Id, MessageRole Role, string Text, MessageStatus Status, DateTimeOffset CreatedAt, IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<ToolStepDto> ToolSteps, UsageDto Usage, string? Model, IReadOnlyList<string> GuardFlags, long? LatencyMs,
    long? TimeToFirstTokenMs);

public sealed record AdminConversationDto(
    Guid Id, string UserId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<AdminMessageDto> Messages);

/// <summary>The used and allowed questions of today.</summary>
public sealed record QuotaDto(int Used, int Limit);

/// <summary>
/// The conversations of the caller (owner = <see cref="ICallerContext.UserId"/>: the user, or for the admin key the
/// <c>X-On-Behalf-Of-User</c>) and the admin's view of the tenant's. Another user's or tenant's conversation is a
/// <see cref="NotFoundException"/>, the same as one that never existed.
/// </summary>
public sealed class ConversationService(IConversationStore store, IQuestionQuota quota, ICallerContext caller, ICacheInvalidator cache, TimeProvider time)
{
    public const int ListLimit = 20;
    private const string NotFound = "The conversation was not found.";

    public async Task<IReadOnlyList<ConversationListItem>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await store.ListAsync(caller.UserId, ListLimit, cancellationToken)).Select(c => new ConversationListItem(c.Id, c.Title, c.CreatedAt, c.UpdatedAt))];

    public async Task<CreatedConversation> CreateAsync(CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new ForbiddenException("A conversation needs a tenant.");
        var conversation = new Conversation(Guid.CreateVersion7(), tenant, caller.UserId, time.GetUtcNow());
        await store.InsertAsync(conversation, cancellationToken);
        await cache.RemoveByTagAsync(ConversationCacheTag.Of(tenant, conversation.Id), cancellationToken);
        return new CreatedConversation(conversation.Id);
    }

    public async Task<ConversationDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var conversation = await store.FindAsync(id, caller.UserId, cancellationToken) ?? throw new NotFoundException(NotFound);
        return new ConversationDto(
            conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt, [.. conversation.Messages.Select(ToDto)]);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await store.DeleteAsync(id, caller.UserId, cancellationToken))
        {
            throw new NotFoundException(NotFound);
        }

        await cache.RemoveByTagAsync(ConversationCacheTag.Of(caller.TenantId, id), cancellationToken);
    }

    public async Task<QuotaDto> QuotaAsync(CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new ForbiddenException("The quota needs a tenant.");
        var count = await quota.GetAsync(tenant, caller.UserId, cancellationToken);
        return new QuotaDto(count.Used, count.Limit);
    }

    public async Task<PagedResult<AdminConversationListItem>> AdminListAsync(string? userId, int page, int size, CancellationToken cancellationToken)
    {
        var (items, total) = await store.ListInTenantAsync(
            string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(), (page - 1) * size, size, cancellationToken);
        return new PagedResult<AdminConversationListItem>(
            [.. items.Select(c => new AdminConversationListItem(c.Id, c.UserId, c.Title, c.CreatedAt, c.UpdatedAt))], page, size, total);
    }

    public async Task<AdminConversationDto> AdminGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var conversation = await store.FindInTenantAsync(id, cancellationToken) ?? throw new NotFoundException(NotFound);
        return new AdminConversationDto(
            conversation.Id, conversation.UserId, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt,
            [.. conversation.Messages.Select(m => new AdminMessageDto(
                m.Id, m.Role, m.Text, m.Status, m.CreatedAt, Sources(m), ToolSteps(m), Usage(m), m.Model, m.GuardFlags, m.LatencyMs,
                m.TimeToFirstTokenMs))]);
    }

    private static MessageDto ToDto(Message m) =>
        new(m.Id, m.Role, m.Text, m.Status, m.CreatedAt, Sources(m), ToolSteps(m), Usage(m), m.Model);

    private static IReadOnlyList<SourceDto> Sources(Message m) =>
        [.. m.Sources.Select(s => new SourceDto(s.Ref, s.ChunkId, s.Type, s.Title, s.SectionPath, s.Reference))];

    private static IReadOnlyList<ToolStepDto> ToolSteps(Message m) =>
        [.. m.ToolSteps.Select(t => new ToolStepDto(t.Id, t.Name, t.Arguments, t.Ok, t.Summary, t.DurationMs))];

    private static UsageDto Usage(Message m) => new(m.Usage.Input, m.Usage.Output);
}
