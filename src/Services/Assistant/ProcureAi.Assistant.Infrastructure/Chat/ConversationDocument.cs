using MongoDB.Bson.Serialization.Attributes;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Assistant.Infrastructure.Chat;

/// <summary>
/// How a conversation is stored in <c>assistantchatdb.conversations</c>: one document with its messages, so an answer is saved in one
/// write. A class of its own, so the domain stays free of MongoDB. IDs are strings ("D" form) and times UTC dates (millisecond precision),
/// so no GUID representation needs configuring and <c>updatedAt</c> sorts as a date.
/// </summary>
internal sealed class ConversationDocument : ITenantDocument
{
    [BsonId]
    public string Id { get; set; } = null!;

    public string TenantId { get; set; } = null!;

    public string UserId { get; set; } = null!;

    public string Title { get; set; } = "";

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    /// <summary>Null when a list read left the messages out (projection).</summary>
    [BsonIgnoreIfNull]
    public List<MessageDocument>? Messages { get; set; }

    public static ConversationDocument From(Conversation conversation) => new()
    {
        Id = conversation.Id.ToString("D"),
        TenantId = conversation.TenantId,
        UserId = conversation.UserId,
        Title = conversation.Title,
        CreatedAt = conversation.CreatedAt.UtcDateTime,
        UpdatedAt = conversation.UpdatedAt.UtcDateTime,
        Messages = [.. conversation.Messages.Select(MessageDocument.From)],
    };

    public Conversation ToDomain() =>
        new(Guid.Parse(Id), TenantId, UserId, Title, Utc(CreatedAt), Utc(UpdatedAt), (Messages ?? []).Select(m => m.ToDomain()));

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed class MessageDocument
{
    public string Id { get; set; } = null!;

    public MessageRole Role { get; set; }

    public string Text { get; set; } = "";

    public MessageStatus Status { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public List<SourceDocument> Sources { get; set; } = [];

    public List<ToolStepDocument> ToolSteps { get; set; } = [];

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public string? Model { get; set; }

    public List<string> GuardFlags { get; set; } = [];

    public long? LatencyMs { get; set; }

    public long? TimeToFirstTokenMs { get; set; }

    /// <summary>Missing in documents stored before it was recorded: false (unchecked).</summary>
    public bool InputChecked { get; set; }

    public static MessageDocument From(Message message) => new()
    {
        Id = message.Id.ToString("D"),
        Role = message.Role,
        Text = message.Text,
        Status = message.Status,
        CreatedAt = message.CreatedAt.UtcDateTime,
        Sources = [.. message.Sources.Select(SourceDocument.From)],
        ToolSteps = [.. message.ToolSteps.Select(ToolStepDocument.From)],
        InputTokens = message.Usage.Input,
        OutputTokens = message.Usage.Output,
        Model = message.Model,
        GuardFlags = [.. message.GuardFlags],
        LatencyMs = message.LatencyMs,
        TimeToFirstTokenMs = message.TimeToFirstTokenMs,
        InputChecked = message.InputChecked,
    };

    public Message ToDomain() =>
        new(
            Guid.Parse(Id), Role, Text, Status, ConversationDocument.Utc(CreatedAt), [.. Sources.Select(s => s.ToDomain())],
            [.. ToolSteps.Select(t => t.ToDomain())], new TokenUsage(InputTokens, OutputTokens), Model, GuardFlags, LatencyMs, TimeToFirstTokenMs,
            InputChecked);
}

internal sealed class SourceDocument
{
    public string Ref { get; set; } = null!;

    public string ChunkId { get; set; } = null!;

    public SourceType Type { get; set; }

    public string Title { get; set; } = "";

    public List<string> SectionPath { get; set; } = [];

    public string? Reference { get; set; }

    public static SourceDocument From(SourceRef source) => new()
    {
        Ref = source.Ref,
        ChunkId = source.ChunkId.ToString("D"),
        Type = source.Type,
        Title = source.Title,
        SectionPath = [.. source.SectionPath],
        Reference = source.Reference,
    };

    public SourceRef ToDomain() => new(Ref, Guid.Parse(ChunkId), Type, Title, SectionPath, Reference);
}

internal sealed class ToolStepDocument
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Arguments { get; set; } = "{}";

    public bool Ok { get; set; }

    public string Summary { get; set; } = "";

    public long DurationMs { get; set; }

    public static ToolStepDocument From(ToolStep step) => new()
    {
        Id = step.Id,
        Name = step.Name,
        Arguments = step.Arguments,
        Ok = step.Ok,
        Summary = step.Summary,
        DurationMs = step.DurationMs,
    };

    public ToolStep ToDomain() => new(Id, Name, Arguments, Ok, Summary, DurationMs);
}
