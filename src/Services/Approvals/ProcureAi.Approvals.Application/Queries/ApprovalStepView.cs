using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Domain;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>
/// A step as one caller sees it: whether they may act on it now, and if not, why. Written by hand rather than with Mapperly,
/// because two of its fields depend on the caller, not on the step.
/// </summary>
public static class ApprovalStepView
{
    /// <summary>Separation of duties: the requester sees their chain locked (screen 8).</summary>
    public const string YouRaisedThisRequest = "You raised this request";

    public const string NotOpen = "This step is not open";

    public const string NotAnApprover = "You are not an approver of this step";

    public static ApprovalStepDto ToDto(ApprovalStep step, string userId, string? requesterName = null)
    {
        var reason = userId == step.RequesterId ? YouRaisedThisRequest
            : !step.IsOpen ? NotOpen
            : !step.MayBeActedOnBy(userId) ? NotAnApprover
            : null;
        return new ApprovalStepDto(
            step.Id,
            step.RequestId,
            step.RequestNumber,
            step.RequestTitle,
            step.RequesterId,
            step.TotalInclVat.Amount,
            step.TotalInclVat.ToDisplayString(),
            step.StepNo,
            step.Level.ToString(),
            step.Status.ToString(),
            step.DueAt,
            reason is null,
            reason,
            step.Version,
            requesterName,
            step.OpenedAt,
            step.Comment);
    }
}
