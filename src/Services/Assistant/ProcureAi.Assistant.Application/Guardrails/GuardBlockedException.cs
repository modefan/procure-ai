namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// A guard stopped a question (<see cref="Stage"/> <c>input</c>) or an answer (<c>output</c>). <see cref="Reason"/> is a
/// <see cref="GuardReasons"/> code. The message never contains the blocked text.
/// </summary>
public sealed class GuardBlockedException(string stage, string reason)
    : Exception($"The {stage} guard blocked the text ({reason}).")
{
    public const string InputStage = "input";
    public const string OutputStage = "output";

    public string Stage { get; } = stage;

    public string Reason { get; } = reason;
}
