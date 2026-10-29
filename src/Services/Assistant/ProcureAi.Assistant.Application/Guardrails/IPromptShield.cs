namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The prompt guard (plan 05 ruling 11, ledger ruling A): checks a user's question before it reaches the model and a document (policy
/// document, invoice) before its text reaches a prompt, so injected instructions never become retrievable context. The registered guard
/// is <see cref="PromptShieldChain"/>: <see cref="PatternPromptShield"/> always, then <see cref="ModelPromptShield"/> when
/// <c>Assistant:Guard:Classifier</c> is <c>Model</c>. Azure AI Content Safety Prompt Shields is a later third implementation (plan 08).
/// </summary>
public interface IPromptShield
{
    Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken);

    Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken);
}
