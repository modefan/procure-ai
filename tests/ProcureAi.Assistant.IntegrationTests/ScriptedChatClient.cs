using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>A model call the provider saw: its task (<c>#task:</c> line), the messages and the names of the tools it was offered.</summary>
public sealed record RecordedCall(string? Task, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Tools)
{
    public string LastUserText => Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    public string AllText => string.Join("\n", Messages.Select(m => m.Text));
}

/// <summary>
/// What the provider (<see cref="ScriptedChatClient"/>) saw. A question of the answer task can steer the Fake with a word in it:
/// <see cref="Slow"/> (a pause of <see cref="SlowDelay"/> before every update), <see cref="Down"/> (the provider is unreachable: an
/// <see cref="HttpRequestException"/> with 503) or <see cref="Hangs"/> (the provider never answers, until the call is cancelled).
/// </summary>
public sealed class ChatScript
{
    public const string Slow = "zzslow";
    public const string Down = "zzdown";
    public const string Hangs = "zzhang";

    /// <summary>The model sends a tool call whose arguments cannot be serialised: the answer flow fails outside any model call.</summary>
    public const string Broken = "zzbroken";

    /// <summary>While <c>get_budget</c> is offered, the model asks for it again and again (one call per turn) for IT-200.</summary>
    public const string Loop = "zzloop";

    /// <summary>The model asks for <c>get_budget</c> six times in its first turn.</summary>
    public const string Burst = "zzburst";

    /// <summary>The model calls <c>search_policy</c> once for <see cref="PolicyLookupQuery"/>, then answers as the Fake.</summary>
    public const string PolicyLookup = "zzpolicy";

    public const string PolicyLookupQuery = "submarines";

    public static readonly TimeSpan SlowDelay = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<RecordedCall> _calls = new();

    public IReadOnlyList<RecordedCall> Calls => [.. _calls];

    internal void Record(RecordedCall call) => _calls.Enqueue(call);
}

/// <summary>The provider's chat client in the tests: the Fake, recorded and steered by <see cref="ChatScript"/>.</summary>
public sealed class ScriptedChatClient(IChatClient inner, ChatScript script) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        script.Record(new RecordedCall(AiTasks.Of(list), list, ToolNames(options)));
        return base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var call = new RecordedCall(AiTasks.Of(list), list, ToolNames(options));
        script.Record(call);
        var question = call.Task == AiTasks.Answer ? call.LastUserText : "";
        if (question.Contains(ChatScript.Down, StringComparison.Ordinal))
        {
            throw new HttpRequestException("The provider is unreachable (test).", null, HttpStatusCode.ServiceUnavailable);
        }

        if (question.Contains(ChatScript.Hangs, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (question.Contains(ChatScript.Broken, StringComparison.Ordinal))
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant, [new FunctionCallContent("call_broken", "lookup", new Dictionary<string, object?> { ["x"] = new Explodes() })]);
            yield break;
        }

        var resultsSinceQuestion = list.SkipWhile(m => m != list.LastOrDefault(x => x.Role == ChatRole.User))
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Count();
        if (Scripted(question, call.Tools, resultsSinceQuestion) is { } calls)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, calls) { FinishReason = ChatFinishReason.ToolCalls };
            yield break;
        }

        var slow = question.Contains(ChatScript.Slow, StringComparison.Ordinal);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            if (slow)
            {
                await Task.Delay(ChatScript.SlowDelay, cancellationToken);
            }

            yield return update;
        }
    }

    private static IReadOnlyList<string> ToolNames(ChatOptions? options) => [.. (options?.Tools ?? []).Select(t => t.Name)];

    /// <summary>The tool calls a scripted question asks for in this turn, or null to let the Fake answer.</summary>
    private static List<AIContent>? Scripted(string question, IReadOnlyList<string> tools, int resultsSinceQuestion)
    {
        static FunctionCallContent Budget() =>
            new("call_" + Guid.NewGuid().ToString("N")[..12], "get_budget", new Dictionary<string, object?> { ["costCenter"] = "IT-200" });

        if (question.Contains(ChatScript.Loop, StringComparison.Ordinal) && tools.Contains("get_budget"))
        {
            return [Budget()];
        }

        if (question.Contains(ChatScript.Burst, StringComparison.Ordinal) && tools.Contains("get_budget") && resultsSinceQuestion == 0)
        {
            return [.. Enumerable.Range(0, 6).Select(_ => Budget())];
        }

        if (question.Contains(ChatScript.PolicyLookup, StringComparison.Ordinal) && tools.Contains("search_policy") && resultsSinceQuestion == 0)
        {
            return
            [
                new FunctionCallContent(
                    "call_" + Guid.NewGuid().ToString("N")[..12], "search_policy",
                    new Dictionary<string, object?> { ["query"] = ChatScript.PolicyLookupQuery }),
            ];
        }

        return null;
    }
}

/// <summary>A value whose JSON serialisation throws.</summary>
public sealed class Explodes
{
    public string Value => throw new InvalidOperationException("This value cannot be serialised (test).");
}
