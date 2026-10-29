using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>
/// One server-sent event of an answer stream: <c>event: &lt;name&gt;</c> and camelCase JSON <c>data</c>. The names are a contract with the
/// Angular client (plan 05 Task 7), which parses them exactly:
/// <list type="bullet">
/// <item><see cref="ToolCall"/> <c>{id, name, arguments}</c> and <see cref="ToolResult"/> <c>{id, ok, summary, durationMs}</c> (tools, Task 5b);</item>
/// <item><see cref="Sources"/> <c>[{ref, title, sectionPath, reference?, type}]</c>, before any text;</item>
/// <item><see cref="Delta"/> <c>{text}</c>: the next piece of the answer;</item>
/// <item><see cref="Replace"/> <c>{text}</c>: the whole answer is replaced (the grounding rule), just before <see cref="Done"/>;</item>
/// <item><see cref="Done"/> <c>{messageId, usage {input, output}, model}</c>: the answer is complete and saved;</item>
/// <item><see cref="Error"/> <c>{title, detail, status}</c>: a ProblemDetails-like failure (422 a guard, 503 the provider), the last event.</item>
/// </list>
/// </summary>
public sealed record SseEvent(string Name, object Data)
{
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string Sources = "sources";
    public const string Delta = "delta";
    public const string Replace = "replace";
    public const string Done = "done";
    public const string Error = "error";

    /// <summary>
    /// camelCase property names, enum values as their names (<c>Policy</c>, as in the REST responses and the generated client), no
    /// indentation (one <c>data:</c> line per event).
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The event's data as JSON.</summary>
    public string DataJson() => JsonSerializer.Serialize(Data, Data.GetType(), JsonOptions);

    public static SseEvent OfSources(IEnumerable<SourceRef> sources) =>
        new(Sources, sources.Select(s => new SourceData(s.Ref, s.Title, s.SectionPath, s.Reference, s.Type)).ToList());

    public static SseEvent OfDelta(string text) => new(Delta, new TextData(text));

    public static SseEvent OfReplace(string text) => new(Replace, new TextData(text));

    public static SseEvent OfDone(Guid messageId, TokenUsage usage, string model) =>
        new(Done, new DoneData(messageId, new UsageData(usage.Input, usage.Output), model));

    public static SseEvent OfError(int status, string title, string? detail = null) => new(Error, new ErrorData(title, detail, status));

    public static SseEvent OfToolCall(string id, string name, JsonElement arguments) => new(ToolCall, new ToolCallData(id, name, arguments));

    public static SseEvent OfToolResult(ToolStep step) => new(ToolResult, new ToolResultData(step.Id, step.Ok, step.Summary, step.DurationMs));
}

/// <summary>One source of the <c>sources</c> event; <see cref="Ref"/> is the marker the answer cites (<c>S1</c>).</summary>
public sealed record SourceData(string Ref, string Title, IReadOnlyList<string> SectionPath, string? Reference, SourceType Type);

public sealed record TextData(string Text);

public sealed record UsageData(long Input, long Output);

public sealed record DoneData(Guid MessageId, UsageData Usage, string Model);

public sealed record ErrorData(string Title, string? Detail, int Status);

public sealed record ToolCallData(string Id, string Name, JsonElement Arguments);

public sealed record ToolResultData(string Id, bool Ok, string Summary, long DurationMs);
