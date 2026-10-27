namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>Whether the approval saga could route a request: is there an eligible approver at every level it needs?</summary>
/// <param name="Possible">True when a chain of eligible approvers exists.</param>
/// <param name="Reason">Why not, when not possible (shown to the requester).</param>
public sealed record ApprovalPreviewResult(bool Possible, string? Reason);

/// <summary>
/// Asks the Approvals service, before a submission, whether the request can be approved at all (so a request never enters a
/// saga that cannot finish). A call that fails or is refused (Approvals or Access down, open circuit, timeout) throws; the
/// API answers it with 503 and <c>Retry-After</c>.
/// </summary>
public interface IApprovalPreview
{
    /// <param name="tenantId">The request's tenant (always the argument, never taken from the caller).</param>
    /// <param name="totalInclVat">Rand incl. VAT.</param>
    /// <param name="requesterId">The requester: separation of duties keeps them out of their own chain.</param>
    Task<ApprovalPreviewResult> CheckAsync(
        string tenantId, string costCenterCode, decimal totalInclVat, string requesterId, CancellationToken cancellationToken);
}
