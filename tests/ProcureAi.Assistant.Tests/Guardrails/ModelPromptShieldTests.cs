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

    [Fact]
    public async Task Provider_content_filter_on_the_classifier_call_blocks_the_question_and_flags_the_document()
    {
        // Azure's own filter can refuse the classifier prompt itself (it carries the attack): that is a block, not a failure.
        var shield = new ModelPromptShield(new FilteringChatClient(), NullLogger<ModelPromptShield>.Instance);

        (await shield.CheckQuestionAsync("an attack", Ct)).ShouldBe(PromptShieldResult.Block(GuardReasons.ProviderFilter));
        (await shield.CheckDocumentAsync("## 1 Notes an attack", Ct)).ShouldBe(PromptShieldResult.Block(GuardReasons.ProviderFilter));
    }

    private sealed class FilteringChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new GuardBlockedException(GuardBlockedException.InputStage, GuardReasons.ProviderFilter);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
