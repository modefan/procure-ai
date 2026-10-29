using ProcureAi.Assistant.Application.Guardrails;

namespace ProcureAi.Assistant.Infrastructure.Guardrails;

/// <summary>
/// The placeholder prompt guard (ledger ruling A): allows every question and document. Plan 05 Task 4 replaces the registration with the
/// real guard (classifier model plus rules).
/// </summary>
internal sealed class AllowAllPromptShield : IPromptShield
{
    public Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(PromptShieldResult.Allowed);

    public Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(PromptShieldResult.Allowed);
}
