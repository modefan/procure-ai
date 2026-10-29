using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.Infrastructure.Ai.Fake;

/// <summary>
/// The Fake chat model (ruling 15): deterministic, no key, useful enough to try the whole flow. It reads the task from the first line of
/// the system message (<c>#task: answer|rewrite|context|classify|scan</c>, <see cref="AiTasks"/>):
/// <list type="bullet">
/// <item><c>answer</c>: first <see cref="FakeIntentRules"/> (a <see cref="FunctionCallContent"/> when a matching tool is offered and has
/// not been called for this question yet); then extractive text: the first sentence of each of the top two sources (lines starting
/// <c>&lt;data id="S1"&gt;</c> blocks, or lines starting <c>[S1]</c>, <c>[S2]</c>, in the prompt), cited, plus a summary of any tool results. Nothing to cite and no tool result: the
/// "policy doesn't cover this" sentence.</item>
/// <item><c>rewrite</c>: the last user message as the standalone question; <c>context</c>: its first sentence;
/// <c>classify</c>: <c>INJECTION</c>, <c>OFF_TOPIC</c> or <c>SAFE</c> from lists of known phrases (the guard classifier); <c>scan</c>: <c>{}</c> (the invoice reader
/// comes with plan 05 Task 6); <c>rerank</c>: the candidates' numbers in the order given (the fusion order is kept).</item>
/// </list>
/// Streaming sends the text word by word with <c>AI:Fake:StreamDelayMs</c> between words. Usage counts words as tokens.
/// </summary>
public sealed partial class FakeChatClient(int streamDelayMs = 0) : IChatClient
{
    public const string NotCovered = "The policy doesn't cover this. Ask your manager or finance.";

    private const int MaxSentenceLength = 300;
    private const int MaxToolSummaryLength = 200;

    /// <summary>
    /// Phrases the Fake classifier flags as prompt injection. Narrow on purpose: "system prompt" or "api key" alone would flag harmless
    /// questions such as "What is our API key policy?" (the red-team set of plan 05 Task 4 has ten that must pass).
    /// </summary>
    private static readonly string[] InjectionPhrases =
    [
        "ignore previous instructions", "ignore all previous", "ignore the above", "disregard your instructions", "your system prompt",
        "your instructions", "you are now", "jailbreak", "developer mode", "can do anything now", "pretend you are", "no restrictions",
        "other tenant", "another tenant", "all tenants", "every tenant",
    ];

    /// <summary>Words of questions that have nothing to do with procurement, which the Fake classifier calls off-topic.</summary>
    private static readonly string[] OffTopicWords =
    [
        "poem", "recipe", "weather", "joke", "rugby", "football", "soccer", "cricket", "song", "lyrics", "horoscope", "movie", "bitcoin",
        "homework",
    ];

    private readonly ChatClientMetadata _metadata = new("fake", null, AiOptions.FakeChatModel);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var reply = Reply(list, options);
        IList<AIContent> contents = reply.Call is null ? [new TextContent(reply.Text)] : [reply.Call];
        var message = new ChatMessage(ChatRole.Assistant, contents);
        return Task.FromResult(new ChatResponse(message)
        {
            ModelId = AiOptions.FakeChatModel,
            ResponseId = Guid.NewGuid().ToString("N"),
            FinishReason = reply.Call is null ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls,
            Usage = Usage(list, reply),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var reply = Reply(list, options);
        var responseId = Guid.NewGuid().ToString("N");
        if (reply.Call is not null)
        {
            yield return Update(responseId, [reply.Call]);
        }
        else
        {
            var words = reply.Text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (i > 0 && streamDelayMs > 0)
                {
                    await Task.Delay(streamDelayMs, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return Update(responseId, [new TextContent(i < words.Length - 1 ? words[i] + " " : words[i])]);
            }
        }

        var last = Update(responseId, [new UsageContent(Usage(list, reply))]);
        last.FinishReason = reply.Call is null ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls;
        yield return last;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private static ChatResponseUpdate Update(string responseId, IList<AIContent> contents) =>
        new(ChatRole.Assistant, contents)
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = AiOptions.FakeChatModel,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private sealed record FakeReply(string Text, FunctionCallContent? Call);

    private static FakeReply Reply(IList<ChatMessage> messages, ChatOptions? options)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        return AiTasks.Of(messages) switch
        {
            AiTasks.Rewrite => new FakeReply(lastUser.Trim(), null),
            AiTasks.Context => new FakeReply(FirstSentence(lastUser), null),
            AiTasks.Classify => new FakeReply(Classify(lastUser), null),
            AiTasks.Scan => new FakeReply("{}", null),
            AiTasks.Rerank => new FakeReply(KeepOrder(lastUser), null),
            _ => Answer(messages, lastUser, options),
        };
    }

    private static FakeReply Answer(IList<ChatMessage> messages, string question, ChatOptions? options)
    {
        var sinceQuestion = messages.Skip(LastIndex(messages, m => m.Role == ChatRole.User) + 1).ToList();
        var results = sinceQuestion.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        var calls = sinceQuestion.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToDictionary(c => c.CallId, c => c.Name);

        // A tool the rules ask for is called once per question; after its result the model answers.
        var offered = (options?.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (results.Count == 0 && FakeIntentRules.Match(question, offered) is { } call)
        {
            return new FakeReply("", new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..12], call.Name, call.Arguments));
        }

        var parts = new List<string>();
        foreach (var (number, text) in Sources(messages).Take(2))
        {
            var sentence = FirstSentence(text);
            if (sentence.Length > 0)
            {
                parts.Add($"{sentence} [S{number}]");
            }
        }

        foreach (var result in results)
        {
            var name = calls.TryGetValue(result.CallId, out var n) ? n : "a tool";
            parts.Add($"From {name}: {Truncate(Flatten(ResultText(result.Result)), MaxToolSummaryLength)}");
        }

        return new FakeReply(parts.Count == 0 ? NotCovered : string.Join(' ', parts), null);
    }

    /// <summary>
    /// The numbered sources of the prompt, by number: the text of each <c>&lt;data id="S&lt;n&gt;" …&gt;…&lt;/data&gt;</c> block (the answer
    /// prompt's format, <c>PromptBuilder</c>), and the text after each line-start marker <c>[S&lt;n&gt;]</c> up to the next marker.
    /// </summary>
    private static IEnumerable<(int Number, string Text)> Sources(IEnumerable<ChatMessage> messages)
    {
        var found = new SortedDictionary<int, string>();
        foreach (var message in messages.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.System))
        {
            var text = message.Text;
            foreach (Match block in DataBlock().Matches(text))
            {
                found.TryAdd(int.Parse(block.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), block.Groups["text"].Value.Trim());
            }

            var markers = SourceMarker().Matches(text);
            for (var i = 0; i < markers.Count; i++)
            {
                var start = markers[i].Index + markers[i].Length;
                var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
                var number = int.Parse(markers[i].Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                found.TryAdd(number, text[start..end].Trim());
            }
        }

        return found.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>The rerank answer that keeps the order: the numbers of the <c>[n]</c> candidates of the prompt, as they come.</summary>
    private static string KeepOrder(string prompt) =>
        string.Join(", ", CandidateMarker().Matches(prompt).Select(m => m.Groups["n"].Value));

    /// <summary>The guard classifier's one-word answer: <c>INJECTION</c>, <c>OFF_TOPIC</c> or <c>SAFE</c>.</summary>
    private static string Classify(string text)
    {
        var lower = Whitespace().Replace(text.ToLowerInvariant(), " ");
        if (InjectionPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
        {
            return "INJECTION";
        }

        return OffTopicWords.Any(w => Regex.IsMatch(lower, $@"\b{w}s?\b")) ? "OFF_TOPIC" : "SAFE";
    }

    private static UsageDetails Usage(IEnumerable<ChatMessage> messages, FakeReply reply)
    {
        long input = messages.Sum(m => (long)WordCount(m.Text));
        long output = reply.Call is null ? WordCount(reply.Text) : 1 + (reply.Call.Arguments?.Count ?? 0);
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output };
    }

    private static int WordCount(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Word().Count(text);

    private static string FirstSentence(string text)
    {
        var flat = Flatten(text);
        var end = SentenceEnd().Match(flat);
        var sentence = end.Success ? flat[..(end.Index + 1)] : flat;
        return Truncate(sentence, MaxSentenceLength);
    }

    private static string Flatten(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    /// <summary>A tool result's text, without the <c>&lt;data kind="tool"&gt;</c> wrapper the Assistant's tools put around it.</summary>
    private static string ResultText(object? result)
    {
        var text = result switch
        {
            null => "",
            string s => s,
            JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : json.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };
        return ToolBlock().Match(text) is { Success: true } block ? block.Groups["text"].Value : text;
    }

    private static int LastIndex(IList<ChatMessage> messages, Func<ChatMessage, bool> predicate)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (predicate(messages[i]))
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"<data\s+id=""S(?<n>\d+)""[^>]*>(?<text>.*?)</data>", RegexOptions.Singleline)]
    private static partial Regex DataBlock();

    [GeneratedRegex(@"^\s*<data\s+kind=""tool""[^>]*>(?<text>.*)</data>\s*$", RegexOptions.Singleline)]
    private static partial Regex ToolBlock();

    [GeneratedRegex(@"(?m)^\s*\[S(?<n>\d+)\]")]
    private static partial Regex SourceMarker();

    [GeneratedRegex(@"(?m)^\[(?<n>\d+)\]")]
    private static partial Regex CandidateMarker();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();
}
