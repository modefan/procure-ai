using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Retrieval;

public sealed class ModelRerankerTests
{
    private static readonly SearchHit[] Hits =
    [
        Hit("Purchases above R 200 000 need finance approval."),
        Hit("Software needs two quotes."),
        Hit("Laptops are bought from the catalogue."),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SearchHit Hit(string text) => new(Guid.NewGuid(), SourceType.Policy, "Policy", ["1 Rules"], text, text, null, 0.01);

    [Fact]
    public async Task Fake_rerank_keeps_the_order()
    {
        var reranker = new ModelReranker(new FakeChatClient(), NullLogger<ModelReranker>.Instance);

        var ranked = await reranker.RerankAsync("who approves large purchases", Hits, Ct);

        ranked.ShouldBe(Hits);
    }

    [Fact]
    public async Task Model_order_is_applied_and_repeated_or_unknown_numbers_are_ignored()
    {
        var reranker = new ModelReranker(new ReplyingChatClient("3, 3, 9, 1"), NullLogger<ModelReranker>.Instance);

        var ranked = await reranker.RerankAsync("laptops", Hits, Ct);

        // 3 then 1 as the model said; the repeat and the unknown 9 are ignored, and 2 (not named) follows in its fusion place.
        ranked.ShouldBe([Hits[2], Hits[0], Hits[1]]);
    }

    [Fact]
    public async Task A_failing_model_keeps_the_fusion_order()
    {
        var reranker = new ModelReranker(new ReplyingChatClient(null), NullLogger<ModelReranker>.Instance);

        var ranked = await reranker.RerankAsync("laptops", Hits, Ct);

        ranked.ShouldBe(Hits);
    }

    [Fact]
    public async Task The_prompt_names_the_rerank_task()
    {
        var client = new ReplyingChatClient("1");
        await new ModelReranker(client, NullLogger<ModelReranker>.Instance).RerankAsync("laptops", Hits, Ct);

        AiTasks.Of(client.Seen!).ShouldBe(AiTasks.Rerank);
    }

    /// <summary>Answers every call with a fixed text, or throws (a provider outage) when it is null.</summary>
    private sealed class ReplyingChatClient(string? reply) : IChatClient
    {
        public IList<ChatMessage>? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = [.. messages];
            return reply is null
                ? throw new HttpRequestException("down")
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
