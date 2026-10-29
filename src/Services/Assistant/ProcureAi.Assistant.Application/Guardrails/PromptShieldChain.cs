namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The registered <see cref="IPromptShield"/>: its shields in order (<see cref="PatternPromptShield"/> first, then the classifier chosen by
/// <c>Assistant:Guard:Classifier</c>). The first block wins, so a text the patterns block never costs a model call.
/// </summary>
public sealed class PromptShieldChain(IReadOnlyList<IPromptShield> shields) : IPromptShield
{
    public IReadOnlyList<IPromptShield> Shields { get; } = shields;

    public Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken) =>
        FirstBlockAsync((shield, ct) => shield.CheckQuestionAsync(text, ct), cancellationToken);

    public Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken) =>
        FirstBlockAsync((shield, ct) => shield.CheckDocumentAsync(text, ct), cancellationToken);

    private async Task<PromptShieldResult> FirstBlockAsync(
        Func<IPromptShield, CancellationToken, Task<PromptShieldResult>> check, CancellationToken cancellationToken)
    {
        foreach (var shield in Shields)
        {
            var result = await check(shield, cancellationToken);
            if (result.Blocked)
            {
                return result;
            }
        }

        return PromptShieldResult.Allowed;
    }
}
