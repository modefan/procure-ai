using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>A model call the provider saw: its task (<c>#task:</c> line) and the messages.</summary>
public sealed record RecordedCall(string? Task, IReadOnlyList<ChatMessage> Messages)
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
        script.Record(new RecordedCall(AiTasks.Of(list), list));
        return base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var call = new RecordedCall(AiTasks.Of(list), list);
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
}

/// <summary>A value whose JSON serialisation throws.</summary>
public sealed class Explodes
{
    public string Value => throw new InvalidOperationException("This value cannot be serialised (test).");
}
