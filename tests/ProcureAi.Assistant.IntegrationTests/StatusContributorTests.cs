using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Chat;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>The Assistant's part of the system status snapshot (plan 06 ruling 10, spec 6.3 item 6): questions and guard blocks today, per tenant.</summary>
[Collection(AssistantCollection.Name)]
public sealed class StatusContributorTests(AssistantApp app)
{
    [Fact]
    public async Task Assistant_counts_guard_blocks_today()
    {
        var acme = AssistantApp.NewTenant();
        var globex = AssistantApp.NewTenant();
        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-2);
        var conversations = app.Services.GetRequiredService<IMongoDatabase>().GetCollection<ConversationDocument>(MongoConversationStore.CollectionName);
        await conversations.InsertManyAsync(
        [
            Conversation(acme, now,
                Message(MessageRole.User, MessageStatus.Complete, now), Message(MessageRole.Assistant, MessageStatus.Complete, now),
                Message(MessageRole.User, MessageStatus.Complete, now), Message(MessageRole.Assistant, MessageStatus.Blocked, now),
                // An older message of a conversation that went on today is not today's.
                Message(MessageRole.User, MessageStatus.Complete, yesterday), Message(MessageRole.Assistant, MessageStatus.Blocked, yesterday)),
            Conversation(acme, now, Message(MessageRole.User, MessageStatus.Complete, now), Message(MessageRole.Assistant, MessageStatus.Blocked, now)),
            Conversation(acme, yesterday, Message(MessageRole.User, MessageStatus.Complete, yesterday), Message(MessageRole.Assistant, MessageStatus.Blocked, yesterday)),
            Conversation(globex, now, Message(MessageRole.User, MessageStatus.Complete, now), Message(MessageRole.Assistant, MessageStatus.Failed, now)),
        ], cancellationToken: TestContext.Current.CancellationToken);

        await using var scope = app.Services.CreateAsyncScope();
        var contributor = scope.ServiceProvider.GetServices<IStatusContributor>().Single(c => c.Name == "assistant");
        var section = await contributor.CollectAsync(TestContext.Current.CancellationToken);

        section.Counters[StatusCounters.Key(acme, StatusCounters.QuestionsToday)].ShouldBe(3);
        section.Counters[StatusCounters.Key(acme, StatusCounters.GuardBlocksToday)].ShouldBe(2);
        section.Counters[StatusCounters.Key(globex, StatusCounters.QuestionsToday)].ShouldBe(1);
        section.Counters.ShouldNotContainKey(StatusCounters.Key(globex, StatusCounters.GuardBlocksToday), "a failed answer is no guard block");
    }

    private static ConversationDocument Conversation(string tenant, DateTime updatedAt, params MessageDocument[] messages) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        TenantId = tenant,
        UserId = "tom",
        Title = "Status test",
        CreatedAt = messages.Min(m => m.CreatedAt),
        UpdatedAt = updatedAt,
        Messages = [.. messages],
    };

    private static MessageDocument Message(MessageRole role, MessageStatus status, DateTime at) =>
        new() { Id = Guid.NewGuid().ToString("D"), Role = role, Status = status, CreatedAt = at, Text = "x" };
}
