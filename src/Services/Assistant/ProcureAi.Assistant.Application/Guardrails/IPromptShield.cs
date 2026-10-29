namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>What a prompt guard decided: <paramref name="Blocked"/> with a short, user-safe <paramref name="Reason"/>, or allowed.</summary>
public sealed record PromptShieldResult(bool Blocked, string? Reason)
{
    public static PromptShieldResult Allowed { get; } = new(false, null);

    public static PromptShieldResult Block(string reason) => new(true, reason);
}

/// <summary>
/// The prompt guard (plan 05 ledger ruling A): checks a user's question before it reaches the model and a policy document before it is
/// indexed, so injected instructions never become retrievable context. Plan 05 Task 4 registers the real guard; until then an allow-all
/// placeholder is registered.
/// </summary>
public interface IPromptShield
{
    Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken);

    Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken);
}
