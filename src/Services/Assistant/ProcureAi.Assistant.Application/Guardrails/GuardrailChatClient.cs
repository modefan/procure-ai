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
/// the update), and the text is passed on only once it is <c>Assistant:Guard:HoldBackChars</c> (64) characters behind the newest text
/// (<see cref="HoldBackBuffer"/>). A secret is found before its match is that long, so on a hit not one character of it has left the guard:
/// the inner stream is disposed (nothing more is read from the model) and <see cref="GuardBlockedException"/> (<c>output</c>) is thrown.
/// Other contents (tool calls, usage) keep their order behind the held text. The rest is passed on when the stream ends without a hit.</item>
/// </list>
/// Every block counts <c>assistant.guard_blocks</c> (stage, reason) and logs a warning with the stage, the reason and the conversation ID
/// only, never the text. The conversation ID is read from <see cref="ChatOptions.AdditionalProperties"/>
/// (<see cref="ConversationIdProperty"/>). Callers must not set <see cref="ChatOptions.ConversationId"/>: in Microsoft.Extensions.AI it
/// names a provider-side stateful conversation (Responses API, Foundry agents), not ours.
/// </summary>
public sealed class GuardrailChatClient(IChatClient inner, IPromptShield shield, AssistantMetrics metrics, ILogger logger, GuardOptions guardOptions)
    : DelegatingChatClient(inner)
{
    /// <summary>The key of <see cref="ChatOptions.AdditionalProperties"/> that carries our conversation ID, for the log.</summary>
    public const string ConversationIdProperty = "conversationId";

    /// <summary>The key of <see cref="ChatOptions.AdditionalProperties"/> for an <see cref="InputCheck"/> the guard marks once the question passed.</summary>
    public const string InputCheckProperty = "inputCheck";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        await CheckInputAsync(list, options, cancellationToken);

        var response = await base.GetResponseAsync(list, WithoutInputCheck(options), cancellationToken);
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
        var held = new HoldBackBuffer(guardOptions.HoldBackChars);
        await using (var updates = base.GetStreamingResponseAsync(list, WithoutInputCheck(options), cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (await updates.MoveNextAsync())
            {
                var update = updates.Current;
                if (output.Inspect(update.Text) is { } reason)
                {
                    // Leaving the block disposes the inner stream before the caller sees the exception; the held text is dropped.
                    throw Blocked(GuardBlockedException.OutputStage, reason, options);
                }

                held.Add(update);
                foreach (var ready in held.Release())
                {
                    yield return ready;
                }
            }
        }

        // The stream ended and every update passed the check: the rest is passed on, in order.
        foreach (var rest in held.Flush())
        {
            yield return rest;
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

        if (options?.AdditionalProperties?.TryGetValue(InputCheckProperty, out var check) == true && check is InputCheck inputCheck)
        {
            inputCheck.Pass();
        }
    }

    /// <summary>The options without the <see cref="InputCheck"/> (it is for the caller, never for the model or the telemetry below).</summary>
    private static ChatOptions? WithoutInputCheck(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.ContainsKey(InputCheckProperty) != true)
        {
            return options;
        }

        var copy = options.Clone();
        copy.AdditionalProperties = new AdditionalPropertiesDictionary(options.AdditionalProperties.Where(p => p.Key != InputCheckProperty));
        return copy;
    }

    private GuardBlockedException Blocked(string stage, string reason, ChatOptions? options)
    {
        metrics.GuardBlock(stage, reason);
        logger.LogWarning("Guard blocked {Stage} {Reason} in conversation {ConversationId}", stage, reason, ConversationId(options) ?? "none");
        return new GuardBlockedException(stage, reason);
    }

    private static string? ConversationId(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ConversationIdProperty, out var id) == true ? id?.ToString() : null;
}
