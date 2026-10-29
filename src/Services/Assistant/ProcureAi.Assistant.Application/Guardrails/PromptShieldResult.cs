namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// What a prompt guard decided: <paramref name="Blocked"/> with a short <paramref name="Reason"/> (a <see cref="GuardReasons"/> code, safe to
/// show and to use as a metric tag), or allowed.
/// </summary>
public sealed record PromptShieldResult(bool Blocked, string? Reason)
{
    public static PromptShieldResult Allowed { get; } = new(false, null);

    public static PromptShieldResult Block(string reason) => new(true, reason);
}
