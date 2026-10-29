namespace ProcureAi.Assistant.Application.Tools;

/// <summary>
/// What one tool call gives the model and the user: <see cref="Text"/> is the plain-text result for the model (cut to
/// <c>Assistant:Tools:MaxResultLength</c> and wrapped as <c>&lt;data kind="tool"&gt;</c> by <see cref="Chat.PromptBuilder.ToolData"/>);
/// <see cref="Summary"/> is the short line of the <c>tool_result</c> event and the saved <c>ToolStep</c> ("R 50 000,00 available").
/// A failure is a result too (<see cref="Error"/>): a tool never throws into the model loop.
/// </summary>
public sealed record ToolResult(bool Ok, string Text, string Summary)
{
    /// <summary>The call added sources to the answer (<c>search_policy</c>): the answer flow sends a further <c>sources</c> event.</summary>
    public bool AddsSources { get; init; }

    public static ToolResult Success(string text, string summary) => new(true, text, summary);

    /// <summary>A failed call: the message is both what the model reads and the summary ("I couldn't check the budget: …").</summary>
    public static ToolResult Error(string message) => new(false, message, message);
}
