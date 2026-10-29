using Microsoft.Extensions.AI;

namespace ProcureAi.Assistant.Application.Ai;

/// <summary>
/// The keys of the two keyed <see cref="IChatClient"/> services. Take them with <c>[FromKeyedServices(AiClients.Answer)]</c>.
/// <list type="bullet">
/// <item><see cref="Answer"/>: the user-facing answer. Pipeline: guardrails (<see cref="IAnswerGuardrail"/>) → default limits → function invocation
/// (at most <c>Assistant:Tools:MaxCallsPerQuestion</c> rounds) → OpenTelemetry → logging → provider.</item>
/// <item><see cref="Utility"/>: internal prompts (query rewrite, chunk context, invoice scan, rerank): default limits → OpenTelemetry →
/// logging → provider. No tools, no guard.</item>
/// <item><see cref="Guard"/>: the guard classifier, the same stages as <see cref="Utility"/> on the guard model.</item>
/// </list>
/// The system message's first line names the task (<see cref="AiTasks"/>), so traces, logs and the Fake model can tell them apart.
/// </summary>
public static class AiClients
{
    public const string Answer = "answer";
    public const string Utility = "utility";

    /// <summary>The guard classifier (<c>#task: classify</c>): the <see cref="Utility"/> stages on <c>AI:Guard:Model</c> (default the chat model).</summary>
    public const string Guard = "guard";

    /// <summary>The ActivitySource (and Meter) name of every model call: <c>ProcureAi.Assistant.AI</c>.</summary>
    public const string TelemetrySource = "ProcureAi.Assistant.AI";
}

/// <summary>The task tags of the first system-message line: <c>#task: answer</c>.</summary>
public static class AiTasks
{
    public const string Prefix = "#task:";
    public const string Answer = "answer";
    public const string Rewrite = "rewrite";
    public const string Context = "context";
    public const string Classify = "classify";
    public const string Scan = "scan";
    public const string Rerank = "rerank";

    /// <summary>The first line of a system message for <paramref name="task"/>.</summary>
    public static string Line(string task) => $"{Prefix} {task}";

    /// <summary>The task named on the first line of the first system message, or null.</summary>
    public static string? Of(IEnumerable<ChatMessage> messages)
    {
        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        if (string.IsNullOrEmpty(system))
        {
            return null;
        }

        var firstLine = system.AsSpan().TrimStart();
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            firstLine = firstLine[..end];
        }

        return firstLine.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? firstLine[Prefix.Length..].Trim().ToString().ToLowerInvariant()
            : null;
    }
}

/// <summary>
/// The outermost stage of the <see cref="AiClients.Answer"/> pipeline: the input and output guards (plan 05 Task 4,
/// <see cref="Guardrails.AnswerGuardrail"/>). A test may register its own to see what reaches the pipeline.
/// </summary>
public interface IAnswerGuardrail
{
    /// <summary>Wraps the rest of the pipeline (function invocation and below).</summary>
    IChatClient Wrap(IChatClient inner);
}
