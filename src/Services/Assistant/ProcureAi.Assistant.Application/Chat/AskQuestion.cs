using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>
/// A question posted to a conversation of the caller: <see cref="AskMode.New"/> adds it, <see cref="AskMode.Regenerate"/> answers the last
/// question again (the text is not used), <see cref="AskMode.Edit"/> replaces the last question (and its answer) with the text.
/// </summary>
public sealed record AskQuestion(Guid ConversationId, string Text, AskMode Mode);

/// <summary>What starting an answer gave: a refusal to answer as a plain HTTP problem, or the event stream (which owns the lock).</summary>
public abstract record AnswerStart
{
    /// <summary>The answer did not start: 404, 409, 422 or 429, with <see cref="RetryAfter"/> for the quota.</summary>
    public sealed record Refused(int Status, string Title, string? Detail = null, TimeSpan? RetryAfter = null) : AnswerStart;

    /// <summary>
    /// The answer stream. Enumerating it releases the conversation's lock at its end; disposing <see cref="Lock"/> releases it when the
    /// stream is never enumerated (the caller registers it for the end of the request; releasing twice is safe).
    /// </summary>
    public sealed record Streaming(IAsyncEnumerable<SseEvent> Events, IAsyncDisposable Lock) : AnswerStart;
}
