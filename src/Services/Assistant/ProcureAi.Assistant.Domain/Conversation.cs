using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Domain;

/// <summary>What a posted message does: ask a new question, answer the last one again, or replace the last question.</summary>
public enum AskMode
{
    New,
    Regenerate,
    Edit,
}

/// <summary>
/// A user's conversation with the assistant, in one tenant: questions and answers in order. Only its owner (<see cref="UserId"/>) reads it
/// (and an admin with <c>admin.system</c>). The title is the first question cut to <see cref="TitleMaxLength"/> characters. A new question
/// is added with <see cref="Ask"/>; <see cref="Regenerate"/> drops the last answer to answer the last question again, and
/// <see cref="EditLastQuestion"/> replaces the last question and drops its answer.
/// </summary>
public sealed class Conversation : Entity<Guid>
{
    public const int TitleMaxLength = 60;

    /// <summary>The most messages one conversation keeps (a stored document has a size limit); then the user starts a new one.</summary>
    public const int MaxMessages = 200;

    private readonly List<Message> _messages;

    public Conversation(Guid id, string tenantId, string userId, DateTimeOffset createdAt)
        : this(id, tenantId, userId, "", createdAt, createdAt, [])
    {
    }

    /// <summary>Rebuilds a stored conversation.</summary>
    public Conversation(
        Guid id, string tenantId, string userId, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt, IEnumerable<Message> messages)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A conversation needs a tenant.");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new DomainException("A conversation needs an owner.");
        }

        Id = id;
        TenantId = tenantId;
        UserId = userId;
        Title = title ?? "";
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        _messages = [.. messages];
    }

    public string TenantId { get; }

    public string UserId { get; }

    public string Title { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Message> Messages => _messages;

    /// <summary>The last question, or null.</summary>
    public Message? LastQuestion => _messages.LastOrDefault(m => m.Role == MessageRole.User);

    /// <summary>Why <paramref name="mode"/> cannot be asked now, or null when it can.</summary>
    public string? CannotAsk(AskMode mode) => mode switch
    {
        AskMode.New when _messages.Count + 2 > MaxMessages => "This conversation is full. Start a new one.",
        AskMode.Regenerate or AskMode.Edit when LastQuestion is null => "There is no question to answer again yet.",
        _ => null,
    };

    /// <summary>Adds a new question (the first one names the conversation).</summary>
    public Message Ask(Guid messageId, string text, DateTimeOffset at)
    {
        ThrowIfCannot(AskMode.New);
        var question = Message.Question(messageId, CheckedText(text), at);
        _messages.Add(question);
        if (Title.Length == 0)
        {
            Title = TitleOf(question.Text);
        }

        UpdatedAt = at;
        return question;
    }

    /// <summary>Drops everything after the last question (its answer), so it is answered again. Returns the last question.</summary>
    public Message Regenerate(DateTimeOffset at)
    {
        ThrowIfCannot(AskMode.Regenerate);
        var question = LastQuestion!;
        var after = _messages.IndexOf(question) + 1;
        _messages.RemoveRange(after, _messages.Count - after);
        UpdatedAt = at;
        return question;
    }

    /// <summary>Replaces the last question (and drops its answer) with <paramref name="text"/>.</summary>
    public Message EditLastQuestion(Guid messageId, string text, DateTimeOffset at)
    {
        ThrowIfCannot(AskMode.Edit);
        var checkedText = CheckedText(text);
        var index = _messages.IndexOf(LastQuestion!);
        _messages.RemoveRange(index, _messages.Count - index);
        var question = Message.Question(messageId, checkedText, at);
        _messages.Add(question);
        if (index == 0)
        {
            Title = TitleOf(question.Text);
        }

        UpdatedAt = at;
        return question;
    }

    /// <summary>Adds the answer to the last question.</summary>
    public void Answer(Message answer, DateTimeOffset at)
    {
        if (answer.Role != MessageRole.Assistant)
        {
            throw new DomainException("An answer is an assistant message.");
        }

        if (_messages.Count == 0 || _messages[^1].Role != MessageRole.User)
        {
            throw new DomainException("An answer follows a question.");
        }

        _messages.Add(answer);
        UpdatedAt = at;
    }

    /// <summary>
    /// Up to <paramref name="count"/> messages before the last question that a follow-up builds on, as question and answer pairs: a
    /// question is kept only with a <see cref="MessageStatus.Complete"/> answer, or a <see cref="MessageStatus.Stopped"/> one whose question the
    /// input guard had already passed (<see cref="Message.InputChecked"/>); the answer too when it has text. A question stopped before the guard
    /// finished was never checked. A question whose answer was blocked or failed, or that was never answered, is left out: a blocked question must never reach
    /// the models again through the history, where the input guard does not look.
    /// </summary>
    public IReadOnlyList<Message> HistoryBeforeLastQuestion(int count)
    {
        var last = LastQuestion;
        var before = last is null ? [.. _messages] : _messages.Take(_messages.IndexOf(last)).ToList();
        var kept = new List<Message>();
        for (var i = 0; i < before.Count; i++)
        {
            if (before[i].Role != MessageRole.User || i + 1 >= before.Count || before[i + 1].Role != MessageRole.Assistant)
            {
                continue;
            }

            var answer = before[i + 1];
            if (answer.Status == MessageStatus.Complete || (answer.Status == MessageStatus.Stopped && answer.InputChecked))
            {
                kept.Add(before[i]);
                if (answer.Text.Length > 0)
                {
                    kept.Add(answer);
                }
            }
        }

        return [.. kept.TakeLast(count)];
    }

    /// <summary>The first question on one line, cut to <see cref="TitleMaxLength"/> characters.</summary>
    public static string TitleOf(string question)
    {
        var flat = string.Join(' ', (question ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= TitleMaxLength ? flat : flat[..TitleMaxLength].TrimEnd();
    }

    private void ThrowIfCannot(AskMode mode)
    {
        if (CannotAsk(mode) is { } reason)
        {
            throw new DomainException(reason);
        }
    }

    private static string CheckedText(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length is 0 or > Message.MaxQuestionLength)
        {
            throw new DomainException($"A question has 1 to {Message.MaxQuestionLength} characters.");
        }

        return trimmed;
    }
}
