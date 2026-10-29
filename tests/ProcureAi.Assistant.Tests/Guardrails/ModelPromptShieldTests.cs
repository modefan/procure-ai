using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureAi.Assistant.Application.Guardrails;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Guardrails;

/// <summary>The classifier's reply must be exactly one of the three words; anything else fails closed.</summary>
public sealed class ModelPromptShieldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("NOT SAFE", null)]
    [InlineData("UNSAFE", null)]
    [InlineData("Unsafe.", null)]
    [InlineData("SAFE? No.", null)]
    [InlineData("I think SAFE", null)]
    [InlineData("", null)]
    [InlineData("SAFE.", ModelPromptShield.Safe)]
    [InlineData("safe", ModelPromptShield.Safe)]
    [InlineData("  \"Safe\"  ", ModelPromptShield.Safe)]
    [InlineData("INJECTION\n", ModelPromptShield.InjectionLabel)]
    [InlineData("Off-topic", ModelPromptShield.OffTopicLabel)]
    [InlineData("OFF TOPIC.", ModelPromptShield.OffTopicLabel)]
    public void Label_requires_the_whole_reply_to_be_one_word(string reply, string? label) =>
        ModelPromptShield.Label(reply).ShouldBe(label);

    [Theory]
    [InlineData("NOT SAFE")]
    [InlineData("UNSAFE")]
    public async Task Negated_reply_blocks_the_question(string reply)
    {
        var shield = new ModelPromptShield(new ReplyingChatClient(reply), NullLogger<ModelPromptShield>.Instance);

        (await shield.CheckQuestionAsync("Who approves laptops?", Ct)).ShouldBe(PromptShieldResult.Block(GuardReasons.ClassifierUnclear));
    }

    private sealed class ReplyingChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
