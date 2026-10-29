using System.Globalization;
using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Infrastructure.Tools;

/// <summary><see cref="IApprovalsLookup"/> over the Approvals API, as the user (<c>AddProcureAiUserClient</c>).</summary>
public sealed class ApprovalsLookup(HttpClient http) : IApprovalsLookup
{
    public const string ServiceName = "approvals-api";

    public async Task<ApprovalPreviewInfo> PreviewAsync(string costCenter, decimal amountInclVat, CancellationToken cancellationToken)
    {
        var preview = await ToolHttp.GetAsync<PreviewAnswer>(
            http,
            $"api/v1/approvals/preview?costCenter={ToolHttp.Escape(costCenter)}&amount={amountInclVat.ToString(CultureInfo.InvariantCulture)}",
            "Approvals",
            cancellationToken);
        return new ApprovalPreviewInfo(
            preview.Possible, preview.Reason,
            [.. (preview.Steps ?? []).Select(s => new ApprovalPreviewStep(s.StepNo, s.Level, s.ApproverNames ?? []))]);
    }

    public async Task<ApprovalChainInfo> ChainAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var chain = await ToolHttp.GetAsync<ChainAnswer>(http, $"api/v1/approvals/requests/{requestId}", "Approvals", cancellationToken);
        return new ApprovalChainInfo(chain.State, [.. (chain.Steps ?? []).Select(s => new ApprovalChainStep(s.StepNo, s.Level, s.Status))]);
    }

    /// <summary>Approvals' <c>ApprovalPreviewDto</c>.</summary>
    private sealed record PreviewAnswer(bool Possible, string? Reason, IReadOnlyList<PreviewStepAnswer>? Steps);

    private sealed record PreviewStepAnswer(int StepNo, string Level, IReadOnlyList<string>? ApproverNames);

    /// <summary>The part of Approvals' <c>ApprovalChainDto</c> the tool shows.</summary>
    private sealed record ChainAnswer(string State, IReadOnlyList<ChainStepAnswer>? Steps);

    private sealed record ChainStepAnswer(int StepNo, string Level, string Status);
}
