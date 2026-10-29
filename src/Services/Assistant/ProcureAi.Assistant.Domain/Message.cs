namespace ProcureAi.Assistant.Domain;

/// <summary>Tokens a model call used.</summary>
public sealed record TokenUsage(long Input, long Output)
{
    public static readonly TokenUsage None = new(0, 0);
}

/// <summary>
/// One message of a <see cref="Conversation"/>: a question (<see cref="MessageRole.User"/>) or an answer (<see cref="MessageRole.Assistant"/>)
/// with the sources it was given, the tools it called, its usage, model, guard flags and timings.
/// </summary>
public sealed class Message
{
    public const int MaxQuestionLength = 2_000;

    public Message(
        Guid id, MessageRole role, string text, MessageStatus status, DateTimeOffset createdAt, IReadOnlyList<SourceRef>? sources = null,
        IReadOnlyList<ToolStep>? toolSteps = null, TokenUsage? usage = null, string? model = null, IReadOnlyList<string>? guardFlags = null,
        long? latencyMs = null, long? timeToFirstTokenMs = null)
    {
        Id = id;
        Role = role;
        Text = text ?? "";
        Status = status;
        CreatedAt = createdAt;
        Sources = sources ?? [];
        ToolSteps = toolSteps ?? [];
        Usage = usage ?? TokenUsage.None;
        Model = model;
        GuardFlags = guardFlags ?? [];
        LatencyMs = latencyMs;
        TimeToFirstTokenMs = timeToFirstTokenMs;
    }

    public Guid Id { get; }

    public MessageRole Role { get; }

    public string Text { get; }

    public MessageStatus Status { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<SourceRef> Sources { get; }

    public IReadOnlyList<ToolStep> ToolSteps { get; }

    public TokenUsage Usage { get; }

    public string? Model { get; }

    /// <summary>What the guards noted, as <c>stage:reason</c> (<c>input:injection</c>, <c>output:email</c>, <c>output:ungrounded</c>).</summary>
    public IReadOnlyList<string> GuardFlags { get; }

    /// <summary>From the question to the end of the answer.</summary>
    public long? LatencyMs { get; }

    /// <summary>From the question to the first streamed answer text; null when none came.</summary>
    public long? TimeToFirstTokenMs { get; }

    public static Message Question(Guid id, string text, DateTimeOffset at) => new(id, MessageRole.User, text, MessageStatus.Complete, at);
}
