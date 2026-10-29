using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Telemetry;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The outermost stage of the <see cref="AiClients.Answer"/> pipeline (<see cref="ChatClientBuilderExtensions.UseGuardrails"/>):
/// <list type="bullet">
/// <item>input: before anything reaches the model, the last user message goes through the <see cref="IPromptShield"/>; a block throws
/// <see cref="GuardBlockedException"/> (<c>input</c>) and the inner client is never called. The answer flow (plan 05 Task 5a) puts the
/// question last and the sources in the system message, so only the user's own words are checked here (sources were checked at ingestion);</item>
/// <item>output: every text update goes through an <see cref="OutputGuard"/> (the last <c>Assistant:Guard:OutputWindow</c> characters plus
/// the update). On a hit the update is not passed on, the inner stream is disposed (nothing more is read from the model) and
/// <see cref="GuardBlockedException"/> (<c>output</c>) is thrown. What was already streamed is the caller's to replace.</item>
/// </list>
/// Every block counts <c>assistant.guard_blocks</c> (stage, reason) and logs a warning with the stage, the reason and the conversation ID
/// only, never the text.
/// </summary>
public sealed class GuardrailChatClient(IChatClient inner, IPromptShield shield, AssistantMetrics metrics, ILogger logger, GuardOptions guardOptions)
    : DelegatingChatClient(inner)
{
    /// <summary>The key of <see cref="ChatOptions.AdditionalProperties"/> that carries the conversation ID when <see cref="ChatOptions.ConversationId"/> is not set.</summary>
    public const string ConversationIdProperty = "conversationId";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        await CheckInputAsync(list, options, cancellationToken);

        var response = await base.GetResponseAsync(list, options, cancellationToken);
        if (SecretPatterns.Find(response.Text) is { } reason)
        {
            throw Blocked(GuardBlockedException.OutputStage, reason, options);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        await CheckInputAsync(list, options, cancellationToken);

        var output = new OutputGuard(guardOptions.OutputWindow);
        await using var updates = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await updates.MoveNextAsync())
        {
            var update = updates.Current;
            if (output.Inspect(update.Text) is { } reason)
            {
                // Leaving the loop disposes the inner stream (await using) before the caller sees the exception.
                throw Blocked(GuardBlockedException.OutputStage, reason, options);
            }

            yield return update;
        }
    }

    private async Task CheckInputAsync(IList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrEmpty(question))
        {
            return;
        }

        var result = await shield.CheckQuestionAsync(question, cancellationToken);
        if (result.Blocked)
        {
            throw Blocked(GuardBlockedException.InputStage, result.Reason ?? GuardReasons.Injection, options);
        }
    }

    private GuardBlockedException Blocked(string stage, string reason, ChatOptions? options)
    {
        metrics.GuardBlock(stage, reason);
        logger.LogWarning("Guard blocked {Stage} {Reason} in conversation {ConversationId}", stage, reason, ConversationId(options) ?? "none");
        return new GuardBlockedException(stage, reason);
    }

    private static string? ConversationId(ChatOptions? options) =>
        options?.ConversationId
        ?? (options?.AdditionalProperties?.TryGetValue(ConversationIdProperty, out var id) == true ? id?.ToString() : null);
}
