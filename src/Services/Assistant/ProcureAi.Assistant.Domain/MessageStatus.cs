namespace ProcureAi.Assistant.Domain;

/// <summary>How an assistant message ended. A question (user message) is always <see cref="Complete"/>.</summary>
public enum MessageStatus
{
    /// <summary>The answer streamed to the end (possibly replaced by the grounding rule).</summary>
    Complete,

    /// <summary>The user stopped it (or left): the text is what was streamed until then.</summary>
    Stopped,

    /// <summary>A guard blocked the question or the answer.</summary>
    Blocked,

    /// <summary>The model provider failed or timed out.</summary>
    Failed,
}

/// <summary>Who wrote a message.</summary>
public enum MessageRole
{
    User,
    Assistant,
}
