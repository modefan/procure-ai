namespace ProcureAi.Assistant.Application.Tools;

/// <summary>One step of a preview: the level (<c>Manager</c>, <c>DepartmentHead</c>, …) and who could approve it.</summary>
public sealed record ApprovalPreviewStep(int StepNo, string Level, IReadOnlyList<string> ApproverNames);

/// <summary>Who would approve a request, or why nobody can (<see cref="Reason"/>).</summary>
public sealed record ApprovalPreviewInfo(bool Possible, string? Reason, IReadOnlyList<ApprovalPreviewStep> Steps);

/// <summary>One step of a request's approval chain; <see cref="Status"/> is Approvals' step status (<c>Pending</c>, <c>Approved</c>, …).</summary>
public sealed record ApprovalChainStep(int StepNo, string Level, string Status);

/// <summary>A request's approval chain: the saga's state (<c>AwaitingApproval</c>, …) and its steps in order.</summary>
public sealed record ApprovalChainInfo(string State, IReadOnlyList<ApprovalChainStep> Steps);

/// <summary>The Approvals service, called as the signed-in user (its permissions and tenant).</summary>
public interface IApprovalsLookup
{
    /// <summary><c>GET api/v1/approvals/preview?costCenter&amp;amount</c> (Rand incl. VAT, for the caller as requester).</summary>
    Task<ApprovalPreviewInfo> PreviewAsync(string costCenter, decimal amountInclVat, CancellationToken cancellationToken);

    /// <summary><c>GET api/v1/approvals/requests/{requestId}</c>; 404 (no chain yet, a draft) is a <see cref="ToolLookupException"/>.</summary>
    Task<ApprovalChainInfo> ChainAsync(Guid requestId, CancellationToken cancellationToken);
}
